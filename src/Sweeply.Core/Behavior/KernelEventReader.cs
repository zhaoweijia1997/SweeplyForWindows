using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Sweeply.Core.Behavior;

/// <summary>
/// Reads the events of Windows' kernel providers (Microsoft-Windows-Kernel-Process, -File, -Registry, -Network) and
/// of its DNS client into <see cref="BehaviorEvent"/>s for the tracked processes, field by field as each event's
/// version lays them out (the layouts come from the providers' manifests). Remembers what later events only point
/// to: which file a file object is, which key a key object is. Writes to a file are added up per file between two
/// <see cref="Flush"/>es. Use from one thread (the ETW consumer).
/// </summary>
public sealed class KernelEventReader(TrackedProcesses tracked, Func<int, string>? commandLine = null)
{
    public static readonly Guid ProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    public static readonly Guid FileProvider = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    public static readonly Guid RegistryProvider = new("70EB4F03-C1DE-4F73-A051-33D13D5413BD");
    public static readonly Guid NetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    public static readonly Guid DnsProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");

    /// <summary>Processes starting and stopping.</summary>
    public const ulong ProcessKeywords = 0x10;

    /// <summary>File create, write, delete and rename (by path), new file; not reads.</summary>
    public const ulong FileKeywords = 0x80 | 0x200 | 0x400 | 0x800 | 0x1000;

    /// <summary>Key create, open (to know keys by name later), delete; value set and delete; not reads.</summary>
    public const ulong RegistryKeywords = 0x1000 | 0x2000 | 0x4000 | 0x100 | 0x200;

    /// <summary>TCP and UDP over IPv4 and IPv6.</summary>
    public const ulong NetworkKeywords = 0x10 | 0x20;

    private const int MaxObjects = 200_000; // file and key objects remembered; cleared when more, as a long recording could pile them up
    private readonly Dictionary<ulong, string> _files = new();
    private readonly Dictionary<ulong, string> _keys = new();
    private readonly Dictionary<(int, ulong), (string Path, long Bytes, DateTime First)> _writes = new();
    private readonly HashSet<(int, string)> _udp = new();
    private readonly Dictionary<(int, string), DateTime> _lookups = new();

    /// <summary>
    /// Whether an event is worth reading at all, before its data is copied: processes starting (one may be a child),
    /// and the rest only from a tracked process. <paramref name="firstField"/> is the data's first 32 bits, where
    /// network and process stop events say which process.
    /// </summary>
    public bool Wanted(Guid provider, int id, int headerProcessId, uint firstField)
    {
        if (provider == ProcessProvider) return id == 1 || id == 2 && tracked.Contains((int)firstField);
        if (provider == FileProvider) return id is 12 or 16 or 26 or 27 or 30 && tracked.Contains(headerProcessId);
        if (provider == RegistryProvider) return id is 1 or 2 or 3 or 5 or 6 && tracked.Contains(headerProcessId);
        if (provider == NetworkProvider) return id is 12 or 15 or 28 or 31 or 42 or 58 && tracked.Contains((int)firstField);
        if (provider == DnsProvider) return id is 3008 or 3018 or 3020;
        return false;
    }

    /// <summary>The event as what a process did, or null when it isn't one of theirs or only told something to remember.</summary>
    public BehaviorEvent? Read(Guid provider, int id, int version, int headerProcessId, DateTime timeUtc, ReadOnlySpan<byte> data)
    {
        var f = new Fields(data);
        if (provider == ProcessProvider) return Process(id, version, timeUtc, ref f);
        if (provider == FileProvider) return File(id, version, headerProcessId, timeUtc, ref f);
        if (provider == RegistryProvider) return Registry(id, headerProcessId, timeUtc, ref f);
        if (provider == NetworkProvider) return Network(id, timeUtc, ref f);
        if (provider == DnsProvider) return Lookup(id, version, headerProcessId, timeUtc, ref f);
        return null;
    }

    /// <summary>The writes added up since the last time: one event per file, with the bytes written.</summary>
    public List<BehaviorEvent> Flush()
    {
        var events = _writes.Select(w => new BehaviorEvent
        {
            Kind = BehaviorKind.FileWritten, ProcessId = w.Key.Item1, Target = w.Value.Path, Size = w.Value.Bytes, TimeUtc = w.Value.First,
        }).OrderBy(e => e.TimeUtc).ToList();
        _writes.Clear();
        return events;
    }

    private BehaviorEvent? Process(int id, int version, DateTime time, ref Fields f)
    {
        int pid = (int)f.U32();
        if (id == 1 && version >= 3 || id == 2 && version >= 2) f.Skip(8); // ProcessSequenceNumber
        if (id == 1)
        {
            f.Skip(8); // CreateTime
            int parent = (int)f.U32();
            if (version >= 3) f.Skip(8);                  // ParentProcessSequenceNumber
            f.Skip(4);                                     // SessionID
            if (version >= 1) f.Skip(4);                  // Flags
            if (version >= 3) { f.Skip(8); f.Sid(); }      // token elevation type, is elevated; MandatoryLabel
            string image = f.Unicode();
            if (!tracked.Started(pid, parent, image)) return null;
            return new BehaviorEvent
            {
                Kind = BehaviorKind.ProcessStarted, ProcessId = pid, Number = parent, Target = image,
                Detail = commandLine?.Invoke(pid) ?? "", TimeUtc = time,
            };
        }
        if (id == 2)
        {
            f.Skip(16); // CreateTime, ExitTime
            int exitCode = (int)f.U32();
            if (!tracked.Exited(pid)) return null;
            return new BehaviorEvent { Kind = BehaviorKind.ProcessExited, ProcessId = pid, Number = exitCode, TimeUtc = time };
        }
        return null;
    }

    private BehaviorEvent? File(int id, int version, int pid, DateTime time, ref Fields f)
    {
        switch (id)
        {
            case 12 or 30: // Create, CreateNewFile: Irp, (ThreadId), FileObject, (IssuingThreadId), CreateOptions, attributes, share, FileName
            {
                f.Skip(8);
                if (version == 0) f.Skip(8);
                ulong fileObject = f.U64();
                if (version >= 1) f.Skip(4);
                uint options = f.U32();
                f.Skip(8);
                string name = f.Unicode();
                if ((options & 1) != 0 || name.Length == 0) return null; // FILE_DIRECTORY_FILE: a folder
                Remember(_files, fileObject, name);
                return id == 30 ? new BehaviorEvent { Kind = BehaviorKind.FileCreated, ProcessId = pid, Target = name, TimeUtc = time } : null;
            }
            case 16: // Write: ByteOffset, Irp, (ThreadId), FileObject, FileKey, (IssuingThreadId), IOSize
            {
                f.Skip(16);
                if (version == 0) f.Skip(8);
                ulong fileObject = f.U64();
                f.Skip(8);
                if (version >= 1) f.Skip(4);
                uint size = f.U32();
                if (!_files.TryGetValue(fileObject, out var path)) return null; // opened before recording began
                var key = (pid, fileObject);
                _writes[key] = _writes.TryGetValue(key, out var w) ? w with { Bytes = w.Bytes + size } : (path, size, time);
                return null;
            }
            case 26 or 27: // DeletePath, RenamePath: Irp, (ThreadId), FileObject, FileKey, ExtraInformation, (IssuingThreadId), InfoClass, FilePath
            {
                f.Skip(version == 0 ? 40 : 32);
                f.Skip(version == 0 ? 4 : 8);
                string path = f.Unicode();
                if (path.Length == 0) return null;
                return new BehaviorEvent { Kind = id == 26 ? BehaviorKind.FileDeleted : BehaviorKind.FileRenamed, ProcessId = pid, Target = path, TimeUtc = time };
            }
        }
        return null;
    }

    private BehaviorEvent? Registry(int id, int pid, DateTime time, ref Fields f)
    {
        switch (id)
        {
            case 1 or 2: // CreateKey, OpenKey: BaseObject, KeyObject, Status, Disposition, BaseName, RelativeName
            {
                ulong baseObject = f.U64(), keyObject = f.U64();
                uint status = f.U32(), disposition = f.U32();
                string baseName = f.Unicode(), relative = f.Unicode();
                if (status != 0) return null;
                string name = KeyPath(baseObject, baseName, relative);
                Remember(_keys, keyObject, name);
                return id == 1 && disposition == 1 /* REG_CREATED_NEW_KEY */
                    ? new BehaviorEvent { Kind = BehaviorKind.KeyCreated, ProcessId = pid, Target = name, TimeUtc = time }
                    : null;
            }
            case 3: // DeleteKey: KeyObject, Status, KeyName
            {
                ulong keyObject = f.U64();
                uint status = f.U32();
                string name = Known(keyObject, f.Unicode());
                return status == 0 ? new BehaviorEvent { Kind = BehaviorKind.KeyDeleted, ProcessId = pid, Target = name, TimeUtc = time } : null;
            }
            case 5: // SetValueKey: KeyObject, Status, Type, DataSize, KeyName, ValueName, CapturedDataSize, CapturedData…
            {
                ulong keyObject = f.U64();
                uint status = f.U32(), type = f.U32();
                f.Skip(4);
                string key = Known(keyObject, f.Unicode()), value = f.Unicode();
                int captured = f.U16();
                string data = ValueText(type, f.Bytes(captured));
                return status == 0 ? new BehaviorEvent { Kind = BehaviorKind.ValueSet, ProcessId = pid, Target = key, Detail = value, Data = data, TimeUtc = time } : null;
            }
            case 6: // DeleteValueKey: KeyObject, Status, KeyName, ValueName
            {
                ulong keyObject = f.U64();
                uint status = f.U32();
                string key = Known(keyObject, f.Unicode()), value = f.Unicode();
                return status == 0 ? new BehaviorEvent { Kind = BehaviorKind.ValueDeleted, ProcessId = pid, Target = key, Detail = value, TimeUtc = time } : null;
            }
        }
        return null;
    }

    /// <summary>A key's full name from what CreateKey/OpenKey give: a base (by name, or by an object opened earlier) and a name relative to it.</summary>
    private string KeyPath(ulong baseObject, string baseName, string relative)
    {
        if (relative.StartsWith(@"\REGISTRY", StringComparison.OrdinalIgnoreCase) || relative.Length == 0 && baseName.Length == 0) return relative;
        string root = baseName.Length > 0 ? baseName : _keys.TryGetValue(baseObject, out var known) ? known : "";
        if (relative.Length == 0) return root;
        return root.Length == 0 ? relative : root.TrimEnd('\\') + @"\" + relative;
    }

    /// <summary>The name an event gives for a key when it is a full one, or else the one remembered for its object.</summary>
    private string Known(ulong keyObject, string name)
    {
        if (name.StartsWith(@"\REGISTRY", StringComparison.OrdinalIgnoreCase)) return name;
        if (_keys.TryGetValue(keyObject, out var known)) return name.Length == 0 ? known : known.TrimEnd('\\') + @"\" + name;
        return name;
    }

    private BehaviorEvent? Network(int id, DateTime time, ref Fields f)
    {
        int pid = (int)f.U32();
        f.Skip(4); // size
        // daddr is the other end, saddr this PC's, for every kind of event; the port is in network byte order.
        bool v6 = id is 28 or 31 or 58;
        var address = f.Bytes(v6 ? 16 : 4);
        if (address.Length != (v6 ? 16 : 4)) return null; // cut short
        var remote = new IPAddress(address);
        f.Skip(v6 ? 16 : 4);
        int remotePort = BinaryPrimitives.ReverseEndianness(f.U16());
        string endpoint = v6 ? $"[{remote}]:{remotePort}" : $"{remote}:{remotePort}";
        bool udp = id is 42 or 58;
        if (udp && !_udp.Add((pid, endpoint))) return null; // only the first datagram to each address
        return new BehaviorEvent
        {
            Kind = id is 15 or 31 ? BehaviorKind.Accepted : BehaviorKind.Connected, ProcessId = pid, Target = endpoint,
            Detail = udp ? "UDP" : "TCP", TimeUtc = time,
        };
    }

    private BehaviorEvent? Lookup(int id, int version, int headerPid, DateTime time, ref Fields f)
    {
        string name = f.Unicode();
        f.Skip(4); // QueryType
        if (id is 3008 or 3018) f.Skip(8); // QueryOptions
        if (id == 3020) f.Skip(8);         // NetworkIndex, InterfaceIndex
        uint status = f.U32();
        string results = f.Unicode();
        int pid = headerPid;
        if (id is 3018 or 3020)
        {
            if (version < 1) return null; // no ClientPID: it would only name the DNS client service
            pid = (int)f.U32();
        }
        if (name.Length == 0 || !tracked.Contains(pid)) return null;
        // The service and the program both say so for one lookup; once within two seconds is enough.
        var key = (pid, name.ToLowerInvariant());
        if (_lookups.TryGetValue(key, out var last) && (time - last).TotalSeconds < 2) return null;
        _lookups[key] = time;
        return new BehaviorEvent { Kind = BehaviorKind.DnsLookup, ProcessId = pid, Target = name, Detail = status == 0 ? results : "", TimeUtc = time };
    }

    private static void Remember(Dictionary<ulong, string> objects, ulong address, string name)
    {
        if (objects.Count >= MaxObjects) objects.Clear();
        objects[address] = name;
    }

    /// <summary>A registry value's data as text: strings as they are, numbers in decimal, binary as hex (all cut short).</summary>
    public static string ValueText(uint type, ReadOnlySpan<byte> data)
    {
        const int Most = 300;
        string text = type switch
        {
            1 or 2 => Encoding.Unicode.GetString(data[..(data.Length & ~1)]).TrimEnd('\0'),                     // REG_SZ, REG_EXPAND_SZ
            7 => string.Join(" | ", Encoding.Unicode.GetString(data[..(data.Length & ~1)]).Split('\0', StringSplitOptions.RemoveEmptyEntries)), // REG_MULTI_SZ
            4 when data.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(data).ToString(System.Globalization.CultureInfo.InvariantCulture),  // REG_DWORD
            11 when data.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(data).ToString(System.Globalization.CultureInfo.InvariantCulture), // REG_QWORD
            _ => Convert.ToHexString(data[..Math.Min(data.Length, 64)]).ToLowerInvariant(),
        };
        return text.Length > Most ? text[..Most] + "…" : text;
    }

    /// <summary>
    /// A registry value's data as it is now, as text like <see cref="ValueText"/>, from the key's kernel path
    /// ("\REGISTRY\MACHINE\…", "\REGISTRY\USER\…"). The kernel's value-set events name the value but come without its
    /// data, so it is read right after; null when the key or value is gone (or can't be read).
    /// </summary>
    public static string? CurrentValue(string kernelKey, string valueName)
    {
        const string machine = @"\REGISTRY\MACHINE\", user = @"\REGISTRY\USER\";
        Microsoft.Win32.RegistryKey root;
        string path;
        if (kernelKey.StartsWith(machine, StringComparison.OrdinalIgnoreCase)) (root, path) = (Microsoft.Win32.Registry.LocalMachine, kernelKey[machine.Length..]);
        else if (kernelKey.StartsWith(user, StringComparison.OrdinalIgnoreCase)) (root, path) = (Microsoft.Win32.Registry.Users, kernelKey[user.Length..]);
        else return null;
        try
        {
            using var key = root.OpenSubKey(path);
            if (key?.GetValue(valueName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) return null;
            string text = value switch
            {
                string s => s,
                string[] lines => string.Join(" | ", lines),
                int dword => ((uint)dword).ToString(System.Globalization.CultureInfo.InvariantCulture),
                long qword => ((ulong)qword).ToString(System.Globalization.CultureInfo.InvariantCulture),
                byte[] bytes => Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 64)).ToLowerInvariant(),
                _ => value.ToString() ?? "",
            };
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Reads an event's fields one after another; past the end, numbers are 0 and strings empty.</summary>
    private ref struct Fields
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _at;

        public Fields(ReadOnlySpan<byte> data) => _data = data;

        public void Skip(int bytes) => _at = Math.Min(_data.Length, _at + bytes);

        public uint U32()
        {
            if (_at + 4 > _data.Length) { _at = _data.Length; return 0; }
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_at..]);
            _at += 4;
            return value;
        }

        public ulong U64()
        {
            if (_at + 8 > _data.Length) { _at = _data.Length; return 0; }
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_at..]);
            _at += 8;
            return value;
        }

        public ushort U16()
        {
            if (_at + 2 > _data.Length) { _at = _data.Length; return 0; }
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_at..]);
            _at += 2;
            return value;
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            count = Math.Max(0, Math.Min(count, _data.Length - _at));
            var bytes = _data.Slice(_at, count);
            _at += count;
            return bytes;
        }

        /// <summary>A UTF-16 string up to its terminating null.</summary>
        public string Unicode()
        {
            int start = _at, end = start;
            while (end + 1 < _data.Length && (_data[end] != 0 || _data[end + 1] != 0)) end += 2;
            string text = Encoding.Unicode.GetString(_data[start..Math.Min(end, _data.Length)]);
            _at = Math.Min(_data.Length, end + 2);
            return text;
        }

        /// <summary>
        /// A SID: revision 1, the number of sub-authorities, six bytes of authority, then four bytes per sub-authority.
        /// Some events put a TOKEN_USER's two pointers in front; those are stepped over.
        /// </summary>
        public void Sid()
        {
            if (_at + 2 <= _data.Length && !(_data[_at] == 1 && _data[_at + 1] <= 15)) Skip(16);
            if (_at + 2 > _data.Length) return;
            Skip(8 + 4 * _data[_at + 1]);
        }
    }
}
