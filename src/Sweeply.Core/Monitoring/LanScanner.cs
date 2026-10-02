using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Sweeply.Core.Monitoring;

/// <summary>A device found on the local network.</summary>
public sealed record LanDevice
{
    public required IPAddress Address { get; init; }

    /// <summary>"24-B2-B9-2C-55-05"; empty when unknown.</summary>
    public string Mac { get; init; } = "";

    /// <summary>The ping's round trip; null when it answered the address question (ARP) but not the ping.</summary>
    public long? PingMs { get; init; }
    public string Name { get; init; } = "";
    public bool IsSelf { get; init; }
    public bool IsGateway { get; init; }

    /// <summary>
    /// A made-up ("locally administered") address: phones and newer PCs use one per network for privacy,
    /// so it says nothing about the maker.
    /// </summary>
    public bool RandomMac => Mac.Length >= 2 && int.TryParse(Mac[..2], System.Globalization.NumberStyles.HexNumber, null, out int first) && (first & 0x02) != 0;
}

/// <summary>Which addresses a scan covers.</summary>
public readonly record struct ScanRange(uint First, uint Last, bool Trimmed)
{
    public int Count => First > Last ? 0 : (int)(Last - First + 1);
    public IPAddress FirstAddress => LanScanner.ToAddress(First);
    public IPAddress LastAddress => LanScanner.ToAddress(Last);
}

/// <summary>
/// Finds the devices on the network one adapter is on. Every address of the adapter's subnet (at most
/// the 254 around this PC's own address) gets one ping; to send it, Windows first asks on the local
/// network who has the address (ARP), so a device that ignores pings still shows up in Windows'
/// neighbour table, with its hardware address. Names come from a reverse DNS lookup and, failing that,
/// a NetBIOS name query (Windows PCs, printers, NAS). Nothing else is sent: no ports are tried.
/// </summary>
public static class LanScanner
{
    private const int Parallel = 64;      // pings in flight at once
    private const int NameTimeout = 1500; // ms

    /// <summary>
    /// The addresses to scan: the subnet without its network and broadcast addresses; a subnet bigger
    /// than a /24 is cut to the /24 around <paramref name="own"/>; /31 and /32 have nothing to scan.
    /// </summary>
    public static ScanRange Range(IPAddress own, int prefixLength)
    {
        uint address = ToUInt(own);
        if (prefixLength >= 31) return new ScanRange(1, 0, false);
        bool trimmed = prefixLength < 24;
        int prefix = trimmed ? 24 : prefixLength;
        uint mask = uint.MaxValue << (32 - prefix);
        uint network = address & mask, broadcast = network | ~mask;
        return new ScanRange(network + 1, broadcast - 1, trimmed);
    }

    public static async Task<IReadOnlyList<LanDevice>> ScanAsync(
        IPAddress own, int prefixLength, int interfaceIndex, string ownMac, IReadOnlyCollection<IPAddress> gateways,
        IProgress<(int Done, int Total)> progress, CancellationToken cancel)
    {
        var range = Range(own, prefixLength);
        var answered = new Dictionary<uint, long>();
        int done = 0;
        using var gate = new SemaphoreSlim(Parallel);
        var pings = new List<Task>();
        for (uint a = range.First; a <= range.Last && range.Count > 0; a++)
        {
            uint address = a;
            if (address == ToUInt(own)) { done++; continue; }
            await gate.WaitAsync(cancel);
            pings.Add(Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var (ms, status, _) = await NetworkTools.PingAsync(ping, ToAddress(address));
                    if (status == IPStatus.Success && ms is long t)
                        lock (answered) answered[address] = t;
                }
                finally
                {
                    gate.Release();
                    progress.Report((Interlocked.Increment(ref done), range.Count));
                }
            }, cancel));
            if (a == uint.MaxValue) break;
        }
        await Task.WhenAll(pings);
        cancel.ThrowIfCancellationRequested();

        // Who answered the address question: neighbours on this adapter inside the range. A device that
        // only answered that (not the ping) must be "reachable" now; for one that answered the ping, any
        // entry that isn't "unreachable" still holds its hardware address.
        var table = Neighbours(interfaceIndex).Where(n => n.Key >= range.First && n.Key <= range.Last).ToDictionary();
        var neighbours = table.Where(n => n.Value.Reachable || answered.ContainsKey(n.Key)).ToDictionary(n => n.Key, n => n.Value.Mac);
        var gatewaySet = gateways.Where(g => g.AddressFamily == AddressFamily.InterNetwork).Select(ToUInt).ToHashSet();
        var found = new List<LanDevice>
        {
            new() { Address = own, Mac = ownMac, IsSelf = true, IsGateway = gatewaySet.Contains(ToUInt(own)) },
        };
        foreach (uint address in answered.Keys.Union(neighbours.Keys).Where(a => a != ToUInt(own)).Order())
            found.Add(new LanDevice
            {
                Address = ToAddress(address),
                Mac = neighbours.TryGetValue(address, out var mac) ? mac : "",
                PingMs = answered.TryGetValue(address, out long ms) ? ms : null,
                IsGateway = gatewaySet.Contains(address),
            });

        // Names, all at once, each given up after a moment.
        var named = await Task.WhenAll(found.Select(async d => d.IsSelf ? d with { Name = Environment.MachineName } : d with { Name = await NameAsync(d.Address, cancel) ?? "" }));
        return named;
    }

    private static async Task<string?> NameAsync(IPAddress address, CancellationToken cancel) =>
        await NetworkTools.NameOfAsync(address, cancel) ?? await NetBiosNameAsync(address, cancel);

    /// <summary>
    /// Asks a device for its NetBIOS names (a node status query to UDP port 137, as "nbtstat -A" does)
    /// and returns its own computer name; null when it doesn't answer within a moment.
    /// </summary>
    public static async Task<string?> NetBiosNameAsync(IPAddress address, CancellationToken cancel)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(NameTimeout);
            await udp.SendAsync(NodeStatusQuery(), new IPEndPoint(address, 137), timeout.Token);
            var reply = await udp.ReceiveAsync(timeout.Token);
            return ParseNodeStatus(reply.Buffer);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    /// <summary>A NetBIOS node status request for the name "*".</summary>
    internal static byte[] NodeStatusQuery()
    {
        var query = new byte[50];
        query[0] = 0x53; query[1] = 0x57; // transaction id
        query[5] = 1;                      // one question
        query[12] = 32;                    // the encoded name is 32 bytes long
        query[13] = (byte)('C'); query[14] = (byte)('K'); // "*" = 0x2A: each half-byte plus 'A'
        for (int i = 15; i < 45; i++) query[i] = (byte)'A'; // then 15 zero bytes, padded the same way
        query[47] = 0x21;                  // type NBSTAT (0x0021, big-endian)
        query[49] = 0x01;                  // class IN (0x0001)
        return query;
    }

    /// <summary>The first unique name with suffix 00 (the computer's own name) from a node status response.</summary>
    internal static string? ParseNodeStatus(byte[] reply)
    {
        const int namesAt = 56; // header 12, name 34, type and class 4, TTL 4, data length 2
        if (reply.Length < namesAt + 1) return null;
        int count = reply[namesAt];
        for (int i = 0; i < count; i++)
        {
            int at = namesAt + 1 + i * 18; // 15 characters, suffix, 2 flag bytes
            if (at + 18 > reply.Length) break;
            byte suffix = reply[at + 15];
            bool group = (reply[at + 16] & 0x80) != 0;
            if (suffix == 0 && !group) return Encoding.ASCII.GetString(reply, at, 15).Trim();
        }
        return null;
    }

    /// <summary>
    /// Windows' IPv4 neighbour table for one adapter, by address: the hardware address, and whether the
    /// entry was confirmed just now ("reachable"). Entries without an address ("unreachable", "incomplete") are left out.
    /// </summary>
    internal static Dictionary<uint, (string Mac, bool Reachable)> Neighbours(int interfaceIndex)
    {
        const int rowSize = 88, indexOffset = 28, macOffset = 40, macLengthOffset = 72, stateOffset = 76;
        const int probe = 2, reachable = 5, permanent = 6; // NL_NEIGHBOR_STATE: 0 unreachable, 1 incomplete, 2 probe, 3 delay, 4 stale
        var result = new Dictionary<uint, (string, bool)>();
        if (GetIpNetTable2(2 /* AF_INET */, out IntPtr table) != 0) return result;
        try
        {
            int count = Marshal.ReadInt32(table, 0);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + 8 + i * rowSize;
                int state = Marshal.ReadInt32(row, stateOffset);
                if (Marshal.ReadInt32(row, indexOffset) != interfaceIndex || state < probe) continue;
                var ip = new byte[4];
                Marshal.Copy(row + 4, ip, 0, 4); // SOCKADDR_IN: family, port, address
                int macLength = Math.Clamp(Marshal.ReadInt32(row, macLengthOffset), 0, 32);
                var mac = new byte[macLength];
                Marshal.Copy(row + macOffset, mac, 0, macLength);
                // Broadcast and multicast entries are permanent too; they are not devices.
                if (mac.Length == 6 && (mac.All(b => b == 0xFF) || (mac[0] & 0x01) != 0)) continue;
                if (mac.Length == 0 || mac.All(b => b == 0)) continue;
                result[(uint)ip[0] << 24 | (uint)ip[1] << 16 | (uint)ip[2] << 8 | ip[3]] = (NetworkAdapters.FormatMac(mac), state is reachable or permanent);
            }
        }
        finally { FreeMibTable(table); }
        return result;
    }

    internal static uint ToUInt(IPAddress v4)
    {
        var b = v4.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    internal static IPAddress ToAddress(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    [DllImport("iphlpapi.dll")] private static extern int GetIpNetTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
}
