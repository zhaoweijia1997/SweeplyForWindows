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
/// Windows' own raw sockets with SIO_RCVALL: every IP packet through an adapter, both ways, without installing
/// anything. Needs administrator rights. Sees the IP layer only — no Ethernet header, no ARP, nothing on
/// 127.0.0.1. Large sends can show up unsplit and without checksums (the network card does both later).
/// </summary>
public sealed class RawSocketSource : IPacketSource
{
    private const int RcvallOn = 1, RcvallIpLevel = 3;

    /// <summary>One socket per adapter and address family: a second one on the same adapter would get every packet again.</summary>
    private sealed record Binding(IPAddress Address, int InterfaceId, byte[][] Own);

    private readonly List<Binding> _bindings = new();
    private readonly List<Socket> _sockets = new();
    private long _received, _dropped;
    private volatile bool _stopped;

    public RawSocketSource(IReadOnlyList<NetworkAdapter> adapters)
    {
        var interfaces = new List<CaptureInterface>();
        foreach (var adapter in adapters)
        {
            int id = interfaces.Count;
            interfaces.Add(new CaptureInterface(LinkType.Raw, adapter.Name, adapter.Description));
            var v4 = adapter.IPv4.Select(a => a.Address).ToList();
            if (v4.Count > 0) _bindings.Add(new Binding(v4[0], id, v4.Select(a => a.GetAddressBytes()).ToArray()));
            var v6 = adapter.IPv6.ToList(); // global addresses first, link-local last
            if (v6.Count > 0) _bindings.Add(new Binding(v6[0], id, v6.Select(a => a.GetAddressBytes()).ToArray()));
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
                // One family or adapter refusing (an IPv6 address that is still being set up, say) leaves the others.
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
            int version = buffer[0] >> 4;
            if (version is not (4 or 6) || length < (version == 4 ? 20 : 40))
            {
                Interlocked.Increment(ref _dropped); // not a whole IP packet
                continue;
            }
            Interlocked.Increment(ref _received);
            var source = version == 4 ? buffer.AsSpan(12, 4) : buffer.AsSpan(8, 16);
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
