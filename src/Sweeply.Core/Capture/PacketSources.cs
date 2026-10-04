using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Capture;

/// <summary>Where live packets come from; runs inside the capture helper.</summary>
public interface IPacketSource : IDisposable
{
    /// <summary>One of the <see cref="HelperRequest"/> backend names.</summary>
    string Backend { get; }

    /// <summary>The interfaces, in the order <see cref="CapturedPacket.InterfaceId"/> counts them; final once started.</summary>
    IReadOnlyList<CaptureInterface> Interfaces { get; }
    long Received { get; }
    long Dropped { get; }

    /// <summary>
    /// Starts delivering packets on background threads until disposed. <paramref name="failed"/> is called once if
    /// it can't start (then before this returns) or stops on its own; no packets follow it.
    /// </summary>
    void Start(Action<CapturedPacket> packet, Action<HelperError> failed);
}

/// <summary>
/// Windows' raw sockets with SIO_RCVALL, the last resort when Windows' packet capture can't start: IPv4 packets
/// through an adapter, both ways. Needs administrator rights. No Ethernet header, no ARP, nothing on 127.0.0.1, and
/// no IPv6 at all: an IPv6 raw socket never gets the IPv6 header, so there would be no addresses to show.
/// Large sends can show up unsplit and without checksums (the network card does both later). The firewall may
/// keep some incoming packets from it.
/// </summary>
public sealed class RawSocketSource : IPacketSource
{
    private const int RcvallOn = 1, RcvallIpLevel = 3;

    /// <summary>One socket per adapter: a second one on the same adapter would get every packet again.</summary>
    private sealed record Binding(IPAddress Address, int InterfaceId, byte[][] Own);

    private readonly List<Binding> _bindings = new();
    private readonly List<Socket> _sockets = new();
    private long _received, _dropped;
    private volatile bool _stopped;

    /// <param name="only">Capture on these adapters (ids) and leave the others to another way (see <see cref="CombinedSource"/>).</param>
    public RawSocketSource(IReadOnlyList<NetworkAdapter> adapters, IReadOnlySet<string>? only = null)
    {
        var interfaces = new List<CaptureInterface>();
        foreach (var adapter in adapters)
        {
            int id = interfaces.Count;
            interfaces.Add(new CaptureInterface(LinkType.Raw, adapter.Name, adapter.Description));
            var v4 = adapter.IPv4.Select(a => a.Address).ToList();
            if (v4.Count > 0 && (only is null || only.Contains(adapter.Id)))
                _bindings.Add(new Binding(v4[0], id, v4.Select(a => a.GetAddressBytes()).ToArray()));
        }
        Interfaces = interfaces;
    }

    public string Backend => HelperRequest.RawSocketBackend;
    public IReadOnlyList<CaptureInterface> Interfaces { get; }
    public long Received => Interlocked.Read(ref _received);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        HelperError? firstError = null;
        foreach (var binding in _bindings)
        {
            Socket socket;
            try { socket = Open(binding.Address); }
            catch (SocketException e)
            {
                // One adapter refusing leaves the others.
                firstError ??= new HelperError(e.SocketErrorCode == SocketError.AccessDenied ? "NeedAdmin" : "SocketFailed", $"{binding.Address}: {e.Message}");
                continue;
            }
            _sockets.Add(socket);
            var thread = new Thread(() => Receive(socket, binding, packet)) { IsBackground = true, Name = "capture " + binding.Address };
            thread.Start();
        }
        if (_sockets.Count == 0) failed(firstError ?? new HelperError("NoAddress", ""));
    }

    private static Socket Open(IPAddress address)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Raw, ProtocolType.IP);
        try
        {
            socket.Bind(new IPEndPoint(address, 0));
            socket.ReceiveBufferSize = 16 * 1024 * 1024;
            // "IP level" gets what this PC sends and receives without putting the card into promiscuous mode;
            // older Windows only knows plain "on".
            try { socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(RcvallIpLevel), new byte[4]); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.InvalidArgument or SocketError.OperationNotSupported)
            {
                socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(RcvallOn), new byte[4]);
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void Receive(Socket socket, Binding binding, Action<CapturedPacket> packet)
    {
        var buffer = new byte[65536];
        while (!_stopped)
        {
            int length;
            try { length = socket.Receive(buffer); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
            if (length <= 0) continue;
            if (buffer[0] >> 4 != 4 || length < 20)
            {
                Interlocked.Increment(ref _dropped); // not a whole IPv4 packet
                continue;
            }
            Interlocked.Increment(ref _received);
            var source = buffer.AsSpan(12, 4);
            bool outbound = false;
            foreach (var own in binding.Own)
                if (source.SequenceEqual(own)) { outbound = true; break; }
            packet(new CapturedPacket
            {
                TimestampUtc = DateTime.UtcNow,
                Link = LinkType.Raw,
                Data = buffer.AsSpan(0, length).ToArray(),
                InterfaceId = binding.InterfaceId,
                Direction = outbound ? PacketDirection.Outbound : PacketDirection.Inbound,
            });
        }
    }

    public void Dispose()
    {
        _stopped = true;
        foreach (var socket in _sockets) socket.Dispose();
        _sockets.Clear();
    }
}

/// <summary>
/// Windows' own packet capture: the NDIS capture filter that "netsh trace … capture=yes" and pktmon use, which
/// Windows has on its network adapters already. It is switched on through an Event Tracing for Windows session of
/// this app's own, and hands over whole frames as they pass the adapter, both ways: Ethernet (802.11 on Wi-Fi) with
/// IPv4 and IPv6 headers, ARP, everything below the firewall. Nothing is installed. Needs the rights to trace
/// (<see cref="EtwSession.CanStart"/>). Doesn't see the loopback (127.0.0.1), which never reaches an adapter.
/// Windows captures on every adapter it has the filter on; the ones not chosen are left out here.
/// </summary>
public sealed class NdisCaptureSource : IPacketSource
{
    /// <summary>Microsoft-Windows-NDIS-PacketCapture.</summary>
    public static readonly Guid Provider = new("2ED6006E-4729-4609-B423-3EE7BCD678EF");
    public const string SessionName = "SweeplyForWindows Capture";

    private readonly Dictionary<uint, int> _interfaceOf; // the adapter's interface index → InterfaceId
    private readonly NdisFrames _frames = new();
    private EtwSession? _session;
    private long _received;
    private int _failed;

    /// <param name="skip">Adapters (ids) left to another way (see <see cref="CombinedSource"/>).</param>
    public NdisCaptureSource(IReadOnlyList<NetworkAdapter> adapters, IReadOnlySet<string>? skip = null)
    {
        Interfaces = adapters.Select(a => new CaptureInterface(LinkType.Ethernet, a.Name, a.Description)).ToList();
        _interfaceOf = new Dictionary<uint, int>();
        for (int i = 0; i < adapters.Count; i++)
            if (skip is null || !skip.Contains(adapters[i].Id)) _interfaceOf[(uint)adapters[i].InterfaceIndex] = i;
    }

    public string Backend => HelperRequest.NdisCapBackend;
    public IReadOnlyList<CaptureInterface> Interfaces { get; }
    public long Received => Interlocked.Read(ref _received);

    /// <summary>What ETW had to drop because its buffers were full (asked of ETW, so not too often).</summary>
    public long Dropped => _session?.EventsLost ?? 0;

    public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        try
        {
            _session = new EtwSession(SessionName, bufferKb: 1024, minBuffers: 8, maxBuffers: 64);
            _session.Enable(Provider, level: 5, matchAnyKeyword: ulong.MaxValue);
            _session.Start((in EtwEvent e) =>
            {
                if (e.Id != NdisFrames.PacketFragment || e.Provider != Provider) return;
                uint adapter = e.ReadUInt32(0); // MiniportIfIndex
                if (!_interfaceOf.TryGetValue(adapter, out int id)) return; // an adapter not chosen
                uint size = e.ReadUInt32(8);    // FragmentSize, then the bytes
                if (size == 0 || 12 + size > e.UserDataLength) return;
                var frame = _frames.Add(adapter, e.Keyword, e.Copy(12, (int)size));
                if (frame is null) return; // a part of a packet; the rest comes in the next events
                var link = NdisFrames.Link(e.Keyword);
                // Wi-Fi frames come already decrypted but still marked "protected"; unmarked, so they read as what they are
                // (here and in Wireshark, once saved), as Microsoft's own etl2pcapng does.
                if (link == LinkType.Ieee80211 && frame.Length > 1) frame[1] &= 0xBF;
                Interlocked.Increment(ref _received);
                packet(new CapturedPacket
                {
                    TimestampUtc = DateTime.FromFileTimeUtc(e.FileTime),
                    Link = link,
                    Data = frame,
                    InterfaceId = id,
                    Direction = NdisFrames.Direction(e.Keyword),
                });
            }, ended =>
            {
                if (ended.Length > 0 && Interlocked.Exchange(ref _failed, 1) == 0) failed(new HelperError("EtwFailed", ended));
            });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _session?.Dispose();
            _session = null;
            failed(new HelperError(e.NativeErrorCode == 5 /* ERROR_ACCESS_DENIED */ ? "NeedAdmin" : "EtwFailed", e.Message));
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// Stops a capture left running by a helper that was ended by force: the session outlives it, and Windows would
    /// go on capturing for nobody until the next capture or a restart. Without the rights to, nothing happens.
    /// </summary>
    public static void StopLeftover()
    {
        if (EtwSession.CanStart) EtwSession.Stop(SessionName);
    }

    private const string FilterClass = @"SYSTEM\CurrentControlSet\Control\Network\{4d36e974-e325-11ce-bfc1-08002be10318}";
    private const string AdapterClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Which of these adapters (ids) Windows' capture filter isn't on, so this way sees nothing there: tunnels (VPNs, a
    /// proxy's TUN mode) and some virtual adapters. Read from the adapters' filter lists in the registry, which anyone
    /// may read; when they can't be read, none (as before this was known).
    /// </summary>
    public static IReadOnlySet<string> Uncovered(IEnumerable<string> adapterIds)
    {
        var uncovered = new HashSet<string>(adapterIds, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var filters = Registry.LocalMachine.OpenSubKey(FilterClass);
            using var adapters = Registry.LocalMachine.OpenSubKey(AdapterClass);
            if (filters is null || adapters is null) return new HashSet<string>();
            string? filter = filters.GetSubKeyNames().FirstOrDefault(name => Value(filters, name, "ComponentId") is "ms_ndiscap");
            if (filter is null) return uncovered; // not on this Windows at all
            foreach (string name in adapters.GetSubKeyNames())
            {
                if (Value(adapters, name, "NetCfgInstanceId") is not string id || !uncovered.Contains(id)) continue;
                if (Value(adapters, name + @"\Linkage", "FilterList") is string[] list
                    && list.Any(f => f.StartsWith($"{id}-{filter}-", StringComparison.OrdinalIgnoreCase)))
                    uncovered.Remove(id);
            }
            return uncovered;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new HashSet<string>();
        }

        // Some keys (the adapters' "Properties") are for administrators only: those are skipped.
        static object? Value(RegistryKey parent, string path, string value)
        {
            try
            {
                using var key = parent.OpenSubKey(path);
                return key?.GetValue(value);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
        }
    }
}

/// <summary>
/// Two ways at once over one list of adapters, each capturing its own share: Windows' packet capture where its filter
/// is, raw sockets on the adapters it doesn't cover (a proxy's TUN adapter, say). Both have to start.
/// </summary>
public sealed class CombinedSource(IPacketSource main, IPacketSource extra) : IPacketSource
{
    private volatile bool _started;

    public string Backend => main.Backend;
    public IReadOnlyList<CaptureInterface> Interfaces => main.Interfaces;
    public long Received => main.Received + extra.Received;
    public long Dropped => main.Dropped + extra.Dropped;

    public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        HelperError? early = null;
        void Failed(HelperError error)
        {
            if (_started) failed(error);
            else early ??= error;
        }
        main.Start(packet, Failed);
        if (early is null) extra.Start(packet, Failed);
        if (early is not null) failed(early);
        else _started = true;
    }

    public void Dispose()
    {
        main.Dispose();
        extra.Dispose();
    }
}

/// <summary>
/// What the NDIS capture events say beyond their bytes, from their keywords: which way the frame went and what kind
/// of frame it is. Also joins frames that come in several events (Windows 8 and older; one event per frame since).
/// </summary>
public sealed class NdisFrames
{
    public const int PacketFragment = 1001;
    public const ulong WirelessWan = 0x200, Native80211 = 0x10000, PacketStart = 0x40000000, PacketEnd = 0x80000000,
        Send = 0x100000000, Receive = 0x200000000;

    private readonly Dictionary<uint, MemoryStream> _parts = new();

    public static PacketDirection Direction(ulong keyword) =>
        (keyword & Send) != 0 ? PacketDirection.Outbound : (keyword & Receive) != 0 ? PacketDirection.Inbound : PacketDirection.Unknown;

    /// <summary>Ethernet, unless the adapter hands over 802.11 frames (Wi-Fi) or bare IP (mobile broadband).</summary>
    public static LinkType Link(ulong keyword) =>
        (keyword & Native80211) != 0 ? LinkType.Ieee80211 : (keyword & WirelessWan) != 0 ? LinkType.Raw : LinkType.Ethernet;

    /// <summary>The whole frame once its last part is in; null while parts are still coming.</summary>
    public byte[]? Add(uint adapter, ulong keyword, byte[] part)
    {
        bool start = (keyword & PacketStart) != 0, end = (keyword & PacketEnd) != 0;
        if (start && end) return part; // the usual case: the whole frame in one event
        if (start || !_parts.TryGetValue(adapter, out var parts)) _parts[adapter] = parts = new MemoryStream();
        parts.Write(part);
        if (!end) return parts.Length > 256 * 1024 ? Drop(adapter) : null; // never ending: give up on it
        _parts.Remove(adapter);
        return parts.ToArray();
    }

    private byte[]? Drop(uint adapter)
    {
        _parts.Remove(adapter);
        return null;
    }
}

/// <summary>
/// Tries ways of capturing in turn: when one can't start, the next takes over (Npcap, then Windows' packet capture,
/// then raw sockets), and <see cref="Note"/> says why the first choice wasn't used.
/// </summary>
public sealed class FallbackSource : IPacketSource
{
    private readonly IReadOnlyList<Func<IPacketSource>> _ways;
    private IPacketSource _current;
    private volatile bool _started;

    public FallbackSource(params Func<IPacketSource>[] ways)
    {
        _ways = ways;
        _current = ways[0]();
    }

    public string Backend => _current.Backend;
    public IReadOnlyList<CaptureInterface> Interfaces => _current.Interfaces;
    public long Received => _current.Received;
    public long Dropped => _current.Dropped;

    /// <summary>Why the first way couldn't start (its error, which names the way); null when it started.</summary>
    public HelperError? Note { get; private set; }

    public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        for (int i = 0; ; i++)
        {
            HelperError? early = null;
            _current.Start(packet, error =>
            {
                if (_started) failed(error); // it stopped later: that's for the app to hear
                else early = error;
            });
            if (early is null)
            {
                _started = true;
                return;
            }
            if (i + 1 >= _ways.Count)
            {
                failed(early);
                return;
            }
            Note ??= early;
            _current.Dispose();
            _current = _ways[i + 1]();
        }
    }

    public void Dispose() => _current.Dispose();
}

/// <summary>Whether Npcap (the capture driver Wireshark installs) is on this PC, and whether it needs administrator rights.</summary>
public static class Npcap
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
    public static bool Installed => File.Exists(Path.Combine(Folder, "wpcap.dll"));

    /// <summary>Installed with "Restrict Npcap driver's access to Administrators only" (then the helper has to be elevated).</summary>
    public static bool AdminOnly
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap\Parameters");
                return key?.GetValue("AdminOnly") is int value && value != 0;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
        }
    }

    /// <summary>Npcap's name for an adapter: "\Device\NPF_{GUID}", the GUID being the adapter's id.</summary>
    public static string DeviceName(string adapterId) => $@"\Device\NPF_{adapterId}";
}

/// <summary>
/// Captures through Npcap when it is installed: whole frames with the Ethernet header, and ARP. Not promiscuous
/// (this PC's own traffic and broadcasts only). Npcap's DLLs are loaded from Npcap's own folder.
/// </summary>
public sealed class NpcapSource : IPacketSource
{
    private readonly List<(string Device, NetworkAdapter Adapter)> _devices;
    private readonly List<IntPtr> _handles = new();
    private readonly List<Thread> _threads = new();
    private long _received, _dropped;
    private volatile bool _stopped;
    private int _failed;

    public NpcapSource(IReadOnlyList<NetworkAdapter> adapters)
    {
        _devices = adapters.Select(a => (Npcap.DeviceName(a.Id), a)).ToList();
        Interfaces = adapters.Select(a => new CaptureInterface(LinkType.Ethernet, a.Name, a.Description)).ToList();
    }

    public string Backend => HelperRequest.NpcapBackend;
    public IReadOnlyList<CaptureInterface> Interfaces { get; private set; }
    public long Received => Interlocked.Read(ref _received);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        try { Native.Load(); }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            failed(new HelperError("NpcapFailed", e.Message));
            return;
        }
        var interfaces = new List<CaptureInterface>();
        var opened = new List<(IntPtr Handle, LinkType Link, int Id)>();
        HelperError? firstError = null;
        foreach (var (device, adapter) in _devices)
        {
            var (handle, link, error) = Open(device);
            if (handle == IntPtr.Zero)
            {
                firstError ??= new HelperError("NpcapFailed", $"{adapter.Name}: {error}");
                continue;
            }
            opened.Add((handle, link, interfaces.Count));
            interfaces.Add(new CaptureInterface(link, adapter.Name, adapter.Description));
        }
        if (opened.Count == 0)
        {
            failed(firstError ?? new HelperError("NoAddress", ""));
            return;
        }
        Interfaces = interfaces;
        foreach (var (handle, link, id) in opened)
        {
            _handles.Add(handle);
            var thread = new Thread(() => Receive(handle, link, id, packet, failed)) { IsBackground = true, Name = "capture npcap " + id };
            _threads.Add(thread);
            thread.Start();
        }
    }

    private static (IntPtr Handle, LinkType Link, string Error) Open(string device)
    {
        var error = new StringBuilder(256);
        IntPtr handle = Native.pcap_create(device, error);
        if (handle == IntPtr.Zero) return (IntPtr.Zero, default, error.ToString());
        Native.pcap_set_snaplen(handle, 262144);
        Native.pcap_set_promisc(handle, 0);
        Native.pcap_set_timeout(handle, 100);
        Native.pcap_set_immediate_mode(handle, 1);
        Native.pcap_set_buffer_size(handle, 16 * 1024 * 1024);
        int status = Native.pcap_activate(handle);
        if (status < 0)
        {
            string message = Marshal.PtrToStringAnsi(Native.pcap_geterr(handle)) ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Native.pcap_close(handle);
            return (IntPtr.Zero, default, message);
        }
        return (handle, (LinkType)Native.pcap_datalink(handle), "");
    }

    private void Receive(IntPtr handle, LinkType link, int interfaceId, Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        while (!_stopped)
        {
            int result = Native.pcap_next_ex(handle, out IntPtr header, out IntPtr data);
            if (result == 0) continue; // nothing within the read timeout
            if (result < 0)
            {
                // -2 is pcap_breakloop on the way out; anything else means the adapter went away.
                if (!_stopped && Interlocked.Exchange(ref _failed, 1) == 0)
                    failed(new HelperError("NpcapFailed", Marshal.PtrToStringAnsi(Native.pcap_geterr(handle)) ?? result.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return;
            }
            // struct pcap_pkthdr: timeval (two 32-bit longs on Windows), caplen, len.
            long seconds = (uint)Marshal.ReadInt32(header), microseconds = (uint)Marshal.ReadInt32(header, 4);
            int captured = Marshal.ReadInt32(header, 8), original = Marshal.ReadInt32(header, 12);
            if (captured <= 0 || captured > 262144) continue;
            var bytes = new byte[captured];
            Marshal.Copy(data, bytes, 0, captured);
            Interlocked.Increment(ref _received);
            packet(new CapturedPacket
            {
                TimestampUtc = DateTime.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + microseconds * 10),
                Link = link,
                Data = bytes,
                OriginalLength = original,
                InterfaceId = interfaceId,
            });
        }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        long dropped = 0;
        foreach (var handle in _handles)
        {
            var stats = new Native.PcapStat();
            if (Native.pcap_stats(handle, ref stats) == 0) dropped += stats.Dropped + stats.InterfaceDropped;
            Native.pcap_breakloop(handle);
        }
        Interlocked.Exchange(ref _dropped, dropped);
        foreach (var thread in _threads) thread.Join(1000); // each read gives up within its 100 ms timeout
        foreach (var handle in _handles) Native.pcap_close(handle);
        _handles.Clear();
    }

    private static class Native
    {
        private static bool _loaded;

        /// <summary>wpcap.dll and the Packet.dll it needs live in System32\Npcap, which isn't on the search path.</summary>
        public static void Load()
        {
            if (_loaded) return;
            SetDllDirectoryW(Npcap.Folder);
            NativeLibrary.Load(Path.Combine(Npcap.Folder, "wpcap.dll"));
            _loaded = true;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PcapStat
        {
            public uint ReceivedCount, Dropped, InterfaceDropped;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectoryW(string path);
        [DllImport("wpcap.dll", CharSet = CharSet.Ansi)] public static extern IntPtr pcap_create(string source, StringBuilder errorBuffer);
        [DllImport("wpcap.dll")] public static extern int pcap_set_snaplen(IntPtr p, int length);
        [DllImport("wpcap.dll")] public static extern int pcap_set_promisc(IntPtr p, int promisc);
        [DllImport("wpcap.dll")] public static extern int pcap_set_timeout(IntPtr p, int milliseconds);
        [DllImport("wpcap.dll")] public static extern int pcap_set_immediate_mode(IntPtr p, int immediate);
        [DllImport("wpcap.dll")] public static extern int pcap_set_buffer_size(IntPtr p, int bytes);
        [DllImport("wpcap.dll")] public static extern int pcap_activate(IntPtr p);
        [DllImport("wpcap.dll")] public static extern int pcap_datalink(IntPtr p);
        [DllImport("wpcap.dll")] public static extern int pcap_next_ex(IntPtr p, out IntPtr header, out IntPtr data);
        [DllImport("wpcap.dll")] public static extern IntPtr pcap_geterr(IntPtr p);
        [DllImport("wpcap.dll")] public static extern int pcap_stats(IntPtr p, ref PcapStat stats);
        [DllImport("wpcap.dll")] public static extern void pcap_breakloop(IntPtr p);
        [DllImport("wpcap.dll")] public static extern void pcap_close(IntPtr p);
    }
}

/// <summary>
/// Plays a capture file back as if it were captured now: for trying the live path without administrator
/// rights, and for tests. Nothing is sent anywhere.
/// </summary>
public sealed class ReplaySource : IPacketSource
{
    private readonly CaptureFile _file;
    private readonly bool _realTime;
    private volatile bool _stopped;
    private long _received;

    /// <param name="realTime">Keep the original gaps (up to half a second each), or go as fast as possible.</param>
    public ReplaySource(string path, bool realTime = true)
    {
        _file = PcapReader.Read(path, new PcapReader.Limits(200_000, 256L * 1024 * 1024));
        _realTime = realTime;
        Interfaces = _file.Interfaces.Count > 0 ? _file.Interfaces : new List<CaptureInterface> { new(LinkType.Ethernet, "replay") };
    }

    public string Backend => HelperRequest.ReplayBackend;
    public IReadOnlyList<CaptureInterface> Interfaces { get; }
    public long Received => Interlocked.Read(ref _received);
    public long Dropped => 0;

    public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
    {
        var thread = new Thread(() =>
        {
            DateTime? previous = null;
            foreach (var p in _file.Packets)
            {
                if (_stopped) return;
                if (_realTime && previous is DateTime before)
                {
                    var gap = p.TimestampUtc - before;
                    if (gap > TimeSpan.Zero) Thread.Sleep(gap > TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : gap);
                }
                previous = p.TimestampUtc;
                Interlocked.Increment(ref _received);
                packet(new CapturedPacket
                {
                    TimestampUtc = DateTime.UtcNow, Link = p.Link, Data = p.Data, OriginalLength = p.OriginalLength,
                    InterfaceId = p.InterfaceId, Direction = p.Direction,
                });
            }
        }) { IsBackground = true, Name = "capture replay" };
        thread.Start();
    }

    public void Dispose() => _stopped = true;
}
