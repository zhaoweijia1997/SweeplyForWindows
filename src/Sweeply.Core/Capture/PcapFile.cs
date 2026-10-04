using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Sweeply.Core.Capture;

/// <summary>
/// Reads capture files in both formats Wireshark writes: classic pcap (microsecond or nanosecond time stamps,
/// either byte order) and pcapng (any number of sections and interfaces, enhanced, simple and obsolete packet
/// blocks, per-interface time resolution and offset). A file cut short ends the reading quietly, with
/// <see cref="CaptureFile.Truncated"/> set.
/// </summary>
public static partial class PcapReader
{
    private const uint PcapMicro = 0xA1B2C3D4, PcapNano = 0xA1B23C4D;
    private const uint PcapNgSection = 0x0A0D0D0A, PcapNgByteOrder = 0x1A2B3C4D;
    private const int MaxBlock = 256 * 1024 * 1024; // larger than any real block: the file is broken

    /// <summary>What a file may bring in at most; the rest is left out (and <see cref="CaptureFile.Truncated"/> set).</summary>
    public readonly record struct Limits(int MaxPackets, long MaxBytes)
    {
        public static Limits None => new(int.MaxValue, long.MaxValue);
    }

    public static CaptureFile Read(string path, Limits? limits = null, CancellationToken cancel = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Read(stream, limits, cancel);
    }

    public static CaptureFile Read(Stream stream, Limits? limits = null, CancellationToken cancel = default)
    {
        var file = new CaptureFile();
        var head = new byte[4];
        if (ReadFully(stream, head) < 4) throw new InvalidDataException("The file is empty.");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var state = new ReadState(limits ?? Limits.None, cancel);
        if (magic == PcapNgSection) ReadPcapNg(stream, head, file, state);
        else if (magic is PcapMicro or PcapNano || BinaryPrimitives.ReverseEndianness(magic) is PcapMicro or PcapNano)
            ReadPcap(stream, magic, file, state);
        else throw new InvalidDataException("This is not a pcap or pcapng file.");
        return file;
    }

    private sealed class ReadState(Limits limits, CancellationToken cancel)
    {
        public Limits Limits { get; } = limits;
        public CancellationToken Cancel { get; } = cancel;
        public long Bytes;

        /// <summary>False when the next packet would go over a limit.</summary>
        public bool Room(CaptureFile file, int length) => file.Packets.Count < Limits.MaxPackets && Bytes + length <= Limits.MaxBytes;
    }

    // ---- classic pcap ----

    private static void ReadPcap(Stream stream, uint magic, CaptureFile file, ReadState state)
    {
        bool swapped = magic is not (PcapMicro or PcapNano);
        bool nano = (swapped ? BinaryPrimitives.ReverseEndianness(magic) : magic) == PcapNano;
        var header = new byte[20];
        if (ReadFully(stream, header) < 20) throw new InvalidDataException("The file header is cut short.");
        uint network = U32(header.AsSpan(16), swapped);
        file.Interfaces.Add(new CaptureInterface((LinkType)(network & 0xFFFF), ""));

        var record = new byte[16];
        while (true)
        {
            state.Cancel.ThrowIfCancellationRequested();
            int got = ReadFully(stream, record);
            if (got == 0) return;
            if (got < 16) { file.Truncated = true; return; }
            uint seconds = U32(record, swapped), fraction = U32(record.AsSpan(4), swapped);
            uint captured = U32(record.AsSpan(8), swapped), original = U32(record.AsSpan(12), swapped);
            if (captured > MaxBlock) { file.Truncated = true; return; }
            if (!state.Room(file, (int)captured)) { file.Truncated = true; return; }
            var data = new byte[captured];
            if (ReadFully(stream, data) < data.Length) { file.Truncated = true; return; }
            long ticks = seconds * TimeSpan.TicksPerSecond + (nano ? fraction / 100 : fraction * 10L);
            file.Packets.Add(new CapturedPacket
            {
                TimestampUtc = DateTime.UnixEpoch.AddTicks(ticks),
                Link = file.Interfaces[0].Link,
                Data = data,
                OriginalLength = (int)Math.Min(original, int.MaxValue),
            });
            state.Bytes += data.Length;
        }
    }

    // ---- pcapng ----

    private sealed class NgInterface
    {
        public LinkType Link;
        public int Id; // index into the file's interface list
        public uint SnapLength;
        public long TicksPerUnitNumerator = 10; // with the default resolution (microseconds): 10 ticks per unit
        public long UnitsPerTickDenominator = 1;
        public long OffsetSeconds;
    }

    private static void ReadPcapNg(Stream stream, byte[] head, CaptureFile file, ReadState state)
    {
        var section = new List<NgInterface>();
        bool bigEndian = false;
        byte[] block = head;
        bool first = true;
        while (true)
        {
            state.Cancel.ThrowIfCancellationRequested();
            if (!first)
            {
                block = new byte[4];
                int got = ReadFully(stream, block);
                if (got == 0) return;
                if (got < 4) { file.Truncated = true; return; }
            }
            first = false;
            var lengthBytes = new byte[4];
            if (ReadFully(stream, lengthBytes) < 4) { file.Truncated = true; return; }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(block);
            if (type == PcapNgSection)
            {
                // The byte-order magic that follows tells how to read this section, its length included.
                var magic = new byte[4];
                if (ReadFully(stream, magic) < 4) { file.Truncated = true; return; }
                uint m = BinaryPrimitives.ReadUInt32LittleEndian(magic);
                if (m == PcapNgByteOrder) bigEndian = false;
                else if (BinaryPrimitives.ReverseEndianness(m) == PcapNgByteOrder) bigEndian = true;
                else throw new InvalidDataException("The pcapng section header is not valid.");
                uint sectionLength = U32(lengthBytes, bigEndian);
                if (sectionLength < 28 || sectionLength > MaxBlock || sectionLength % 4 != 0) { file.Truncated = true; return; }
                var rest = new byte[sectionLength - 12];
                if (ReadFully(stream, rest) < rest.Length) { file.Truncated = true; return; }
                section.Clear(); // interface ids start again in each section
                foreach (var (code, value) in Options(rest.AsSpan(12, rest.Length - 16), bigEndian))
                    if (code == 4 /* shb_userappl */) file.Application ??= Utf8(value);
                continue;
            }

            uint length = U32(lengthBytes, bigEndian);
            if (length < 12 || length > MaxBlock || length % 4 != 0) { file.Truncated = true; return; }
            var body = new byte[length - 12];
            if (ReadFully(stream, body) < body.Length) { file.Truncated = true; return; }
            var trailer = new byte[4];
            if (ReadFully(stream, trailer) < 4) { file.Truncated = true; return; }
            type = bigEndian ? BinaryPrimitives.ReverseEndianness(type) : type;

            switch (type)
            {
                case 1: // interface description
                    if (body.Length < 8) break;
                    var ni = new NgInterface
                    {
                        Link = (LinkType)U16(body, bigEndian),
                        SnapLength = U32(body.AsSpan(4), bigEndian),
                        Id = file.Interfaces.Count,
                    };
                    string name = "", description = "";
                    foreach (var (code, value) in Options(body.AsSpan(8), bigEndian))
                    {
                        if (code == 2) name = Utf8(value);
                        else if (code == 3) description = Utf8(value);
                        else if (code == 9 && value.Length >= 1) SetResolution(ni, value[0]);
                        else if (code == 14 && value.Length >= 8) ni.OffsetSeconds = (long)U64(value, bigEndian);
                    }
                    section.Add(ni);
                    file.Interfaces.Add(new CaptureInterface(ni.Link, name, description));
                    break;

                case 6: // enhanced packet
                case 2: // obsolete packet
                {
                    if (body.Length < 20) break;
                    bool enhanced = type == 6;
                    int id = enhanced ? (int)U32(body, bigEndian) : U16(body, bigEndian);
                    if (id < 0 || id >= section.Count) break;
                    uint high = U32(body.AsSpan(4), bigEndian), low = U32(body.AsSpan(8), bigEndian);
                    uint captured = U32(body.AsSpan(12), bigEndian), original = U32(body.AsSpan(16), bigEndian);
                    if (captured > body.Length - 20) break;
                    if (!state.Room(file, (int)captured)) { file.Truncated = true; return; }
                    int optionsAt = 20 + Pad4((int)captured);
                    var direction = PacketDirection.Unknown;
                    string? comment = null;
                    if (optionsAt <= body.Length)
                    {
                        foreach (var (code, value) in Options(body.AsSpan(optionsAt), bigEndian))
                        {
                            if (code == 1) comment = comment is null ? Utf8(value) : comment + "\n" + Utf8(value);
                            else if (code == 2 && value.Length >= 4 && enhanced)
                                direction = (U32(value, bigEndian) & 3) switch { 1 => PacketDirection.Inbound, 2 => PacketDirection.Outbound, _ => PacketDirection.Unknown };
                        }
                    }
                    AddPacket(file, state, section[id], ((ulong)high << 32) | low, body.AsSpan(20, (int)captured), original, direction, comment);
                    break;
                }

                case 3: // simple packet: original length, then data, on the first interface
                {
                    if (body.Length < 4 || section.Count == 0) break;
                    uint original = U32(body, bigEndian);
                    int captured = (int)Math.Min(original, (uint)(body.Length - 4));
                    if (section[0].SnapLength > 0) captured = (int)Math.Min(captured, section[0].SnapLength);
                    if (!state.Room(file, captured)) { file.Truncated = true; return; }
                    AddPacket(file, state, section[0], 0, body.AsSpan(4, captured), original, PacketDirection.Unknown, null);
                    break;
                }
                // Name resolution, interface statistics, decryption secrets and others: not needed here.
            }
        }
    }

    private static void AddPacket(CaptureFile file, ReadState state, NgInterface ni, ulong units, ReadOnlySpan<byte> data,
        uint original, PacketDirection direction, string? comment)
    {
        long ticks = ni.UnitsPerTickDenominator > 1 ? (long)(units / (ulong)ni.UnitsPerTickDenominator) : (long)units * ni.TicksPerUnitNumerator;
        ticks += ni.OffsetSeconds * TimeSpan.TicksPerSecond;
        DateTime time;
        try { time = DateTime.UnixEpoch.AddTicks(ticks); }
        catch (ArgumentOutOfRangeException) { time = DateTime.UnixEpoch; }
        var (processName, processId, rest) = ProcessFromComment(comment);
        file.Packets.Add(new CapturedPacket
        {
            TimestampUtc = time,
            Link = ni.Link,
            Data = data.ToArray(),
            OriginalLength = (int)Math.Min(original, int.MaxValue),
            InterfaceId = ni.Id,
            Direction = direction,
            ProcessName = processName,
            ProcessId = processId,
            Comment = rest,
        });
        state.Bytes += data.Length;
    }

    /// <summary>if_tsresol: the low 7 bits are a power of 10 (or of 2 with the high bit set) dividing a second.</summary>
    private static void SetResolution(NgInterface ni, byte resolution)
    {
        int exponent = resolution & 0x7F;
        bool power2 = (resolution & 0x80) != 0;
        if (power2)
        {
            // Rare; convert through seconds at the nearest decimal resolution that fits a tick.
            double ticksPerUnit = TimeSpan.TicksPerSecond / Math.Pow(2, exponent);
            if (ticksPerUnit >= 1) { ni.TicksPerUnitNumerator = (long)ticksPerUnit; ni.UnitsPerTickDenominator = 1; }
            else { ni.TicksPerUnitNumerator = 1; ni.UnitsPerTickDenominator = (long)Math.Round(1 / ticksPerUnit); }
            return;
        }
        if (exponent <= 7) { ni.TicksPerUnitNumerator = (long)Math.Pow(10, 7 - exponent); ni.UnitsPerTickDenominator = 1; }
        else { ni.TicksPerUnitNumerator = 1; ni.UnitsPerTickDenominator = (long)Math.Pow(10, Math.Min(exponent - 7, 18)); }
    }

    /// <summary>The options of a block: code, length, value padded to 4 bytes; until "end of options" or the end.</summary>
    private static List<(ushort Code, byte[] Value)> Options(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var result = new List<(ushort, byte[])>();
        int at = 0;
        while (at + 4 <= data.Length)
        {
            ushort code = U16(data[at..], bigEndian);
            int length = U16(data[(at + 2)..], bigEndian);
            if (code == 0 || at + 4 + length > data.Length) break;
            result.Add((code, data.Slice(at + 4, length).ToArray()));
            at += 4 + Pad4(length);
        }
        return result;
    }

    [GeneratedRegex(@"^SweeplyForWindows process: (?<name>.+?) \((?<pid>\d+)\)$", RegexOptions.Multiline)]
    private static partial Regex ProcessComment();

    /// <summary>The program this app wrote into a packet comment, and whatever else the comment says.</summary>
    internal static (string? Name, int Id, string? Other) ProcessFromComment(string? comment)
    {
        if (comment is null) return (null, 0, null);
        var match = ProcessComment().Match(comment);
        if (!match.Success) return (null, 0, comment);
        string rest = (comment[..match.Index] + comment[(match.Index + match.Length)..]).Trim('\n');
        return (match.Groups["name"].Value, int.Parse(match.Groups["pid"].Value), rest.Length == 0 ? null : rest);
    }

    internal static string ProcessCommentText(string name, int id) => $"SweeplyForWindows process: {name} ({id})";

    private static int Pad4(int length) => (length + 3) & ~3;
    private static ushort U16(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
    private static uint U32(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
    private static ulong U64(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);
    private static string Utf8(byte[] value) => Encoding.UTF8.GetString(value).TrimEnd('\0');

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>
/// Writes pcapng, which Wireshark opens: one section, an interface block per interface, and an enhanced packet
/// block per packet with 100 ns time stamps, the direction, and the program (as a packet comment) when known.
/// </summary>
public sealed class PcapNgWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Dictionary<(LinkType, string), int> _interfaces = new();

    public PcapNgWriter(Stream stream, string application, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        var body = new MemoryStream();
        Write32(body, 0x1A2B3C4D);   // byte-order magic
        Write16(body, 1);            // version 1.0
        Write16(body, 0);
        Write64(body, ulong.MaxValue); // section length not given
        Option(body, 4, Encoding.UTF8.GetBytes(application)); // shb_userappl
        EndOptions(body);
        Block(0x0A0D0D0A, body.ToArray());
    }

    /// <summary>The id of the interface with this link type and name, written the first time it is used.</summary>
    public int Interface(LinkType link, string name)
    {
        if (_interfaces.TryGetValue((link, name), out int id)) return id;
        var body = new MemoryStream();
        Write16(body, (ushort)link);
        Write16(body, 0);
        Write32(body, 0); // no snap length limit
        if (name.Length > 0) Option(body, 2, Encoding.UTF8.GetBytes(name)); // if_name
        Option(body, 9, new byte[] { 7 });   // if_tsresol: 10^-7 s, the resolution of DateTime
        EndOptions(body);
        Block(1, body.ToArray());
        id = _interfaces.Count;
        _interfaces[(link, name)] = id;
        return id;
    }

    public void Write(CapturedPacket packet, int interfaceId)
    {
        var body = new MemoryStream(packet.Data.Length + 64);
        ulong units = (ulong)Math.Max(0, (packet.TimestampUtc - DateTime.UnixEpoch).Ticks);
        Write32(body, (uint)interfaceId);
        Write32(body, (uint)(units >> 32));
        Write32(body, (uint)units);
        Write32(body, (uint)packet.Data.Length);
        Write32(body, (uint)packet.Length);
        body.Write(packet.Data);
        Pad(body, packet.Data.Length);
        string? comment = packet.ProcessName is { Length: > 0 } name ? PcapReader.ProcessCommentText(name, packet.ProcessId) : null;
        if (packet.Comment is { Length: > 0 } other) comment = comment is null ? other : comment + "\n" + other;
        if (comment is not null) Option(body, 1, Encoding.UTF8.GetBytes(comment)); // opt_comment
        if (packet.Direction != PacketDirection.Unknown)
        {
            var flags = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(flags, packet.Direction == PacketDirection.Inbound ? 1u : 2u);
            Option(body, 2, flags); // epb_flags: the direction in the low two bits
        }
        EndOptions(body);
        Block(6, body.ToArray());
    }

    public void Dispose()
    {
        _stream.Flush();
        if (!_leaveOpen) _stream.Dispose();
    }

    private void Block(uint type, byte[] body)
    {
        var header = new byte[8];
        uint total = (uint)(body.Length + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), total);
        _stream.Write(header);
        _stream.Write(body);
        _stream.Write(header.AsSpan(4, 4));
    }

    private static void Option(MemoryStream body, ushort code, byte[] value)
    {
        Write16(body, code);
        Write16(body, (ushort)value.Length);
        body.Write(value);
        Pad(body, value.Length);
    }

    private static void EndOptions(MemoryStream body)
    {
        Write16(body, 0);
        Write16(body, 0);
    }

    private static void Pad(MemoryStream body, int length)
    {
        for (int i = length; i % 4 != 0; i++) body.WriteByte(0);
    }

    private static void Write16(MemoryStream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }
    private static void Write32(MemoryStream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void Write64(MemoryStream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); s.Write(b); }
}
