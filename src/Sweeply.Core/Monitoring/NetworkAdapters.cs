using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Sweeply.Core.Monitoring;

/// <summary>The Wi-Fi network an adapter is connected to.</summary>
public sealed record WifiConnection
{
    /// <summary>Empty when Windows keeps it back (newer Windows 11 asks for location permission first).</summary>
    public string Ssid { get; init; } = "";
    public string Bssid { get; init; } = "";
    public int SignalPercent { get; init; }
    public int? RssiDbm { get; init; }
    public int? Channel { get; init; }
    public int? FrequencyMhz { get; init; }
    public int PhyType { get; init; }
    public double ReceiveMbps { get; init; }
    public double TransmitMbps { get; init; }
}

/// <summary>One network adapter as Windows sees it right now.</summary>
public sealed record NetworkAdapter
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public bool IsWifi { get; init; }
    public bool IsUp { get; init; }

    /// <summary>A real network card (Wi-Fi, Ethernet port), not a virtual adapter (VMware, VPN tunnel, Bluetooth, Wi-Fi Direct).</summary>
    public bool IsHardware { get; init; }
    public long SpeedBitsPerSecond { get; init; }
    public string MacAddress { get; init; } = "";
    public IReadOnlyList<(IPAddress Address, int PrefixLength)> IPv4 { get; init; } = Array.Empty<(IPAddress, int)>();
    public IReadOnlyList<IPAddress> IPv6 { get; init; } = Array.Empty<IPAddress>();
    public IReadOnlyList<IPAddress> Gateways { get; init; } = Array.Empty<IPAddress>();
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = Array.Empty<IPAddress>();
    public bool DhcpEnabled { get; init; }
    public IReadOnlyList<IPAddress> DhcpServers { get; init; } = Array.Empty<IPAddress>();

    /// <summary>Since the adapter came up (usually since Windows started).</summary>
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public WifiConnection? Wifi { get; init; }
}

/// <summary>
/// Lists the network adapters with their addresses, and for Wi-Fi the network, signal and channel.
/// Only reads what Windows already knows; nothing is sent on the network.
/// </summary>
public static class NetworkAdapters
{
    public static IReadOnlyList<NetworkAdapter> Read()
    {
        Dictionary<Guid, WifiConnection> wifi;
        try { wifi = Wlan.CurrentConnections(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { wifi = new(); } // no Wi-Fi service (some servers)

        var result = new List<NetworkAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Not shown: loopback; cards that were plugged in once but are not now; and the layers
            // Windows stacks on a card (WFP and QoS filters, WAN miniports), which have no IP stack
            // of their own and only repeat their card.
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus == OperationalStatus.NotPresent) continue;
            try
            {
                if (InterfaceIndex(nic) > 0) result.Add(Describe(nic, wifi));
            }
            catch (NetworkInformationException) { } // gone while being read
        }
        // Real, connected cards first; then the other real ones; then the virtual ones.
        return result.OrderByDescending(a => a.IsHardware && a.IsUp).ThenByDescending(a => a.IsHardware).ThenByDescending(a => a.IsUp).ToList();
    }

    private static NetworkAdapter Describe(NetworkInterface nic, Dictionary<Guid, WifiConnection> wifi)
    {
        var ip = nic.GetIPProperties();
        int index = InterfaceIndex(nic);
        bool hasGuid = Guid.TryParse(nic.Id, out var guid);
        var unicast = ip.UnicastAddresses;
        var stats = nic.GetIPStatistics();
        bool dhcp = false;
        try { dhcp = ip.GetIPv4Properties()?.IsDhcpEnabled ?? false; }
        catch (NetworkInformationException) { } // IPv4 not bound

        return new NetworkAdapter
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            IsWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            IsHardware = index > 0 && IsHardwareInterface(index),
            SpeedBitsPerSecond = nic.Speed > 0 ? nic.Speed : 0,
            MacAddress = FormatMac(nic.GetPhysicalAddress().GetAddressBytes()),
            IPv4 = unicast.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                          .Select(u => (u.Address, u.PrefixLength)).ToList(),
            // Global addresses first, the link-local fe80:: last.
            IPv6 = unicast.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                          .Select(u => u.Address).OrderBy(a => a.IsIPv6LinkLocal).ToList(),
            Gateways = ip.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToList(),
            DnsServers = ip.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).ToList(),
            DhcpEnabled = dhcp,
            DhcpServers = ip.DhcpServerAddresses.ToList(),
            BytesReceived = stats.BytesReceived,
            BytesSent = stats.BytesSent,
            Wifi = hasGuid && wifi.TryGetValue(guid, out var w) && nic.OperationalStatus == OperationalStatus.Up ? w : null,
        };
    }

    /// <summary>The interface index of the adapter's IP stack; 0 for a layer without one (filters, miniports).</summary>
    internal static int InterfaceIndex(NetworkInterface nic)
    {
        try
        {
            var ip = nic.GetIPProperties();
            try { if (ip.GetIPv4Properties() is { } v4) return v4.Index; }
            catch (NetworkInformationException) { }
            try { if (ip.GetIPv6Properties() is { } v6) return v6.Index; }
            catch (NetworkInformationException) { }
        }
        catch (NetworkInformationException) { }
        return 0;
    }

    /// <summary>Whether an IPv4 address is on the network of one of the adapters that are up (same subnet).</summary>
    public static bool IsOnLocalNetwork(IPAddress address, IEnumerable<NetworkAdapter> adapters)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        uint target = ToUInt(address);
        foreach (var adapter in adapters.Where(a => a.IsUp))
            foreach (var (own, prefix) in adapter.IPv4)
            {
                uint mask = prefix <= 0 ? 0 : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
                if ((ToUInt(own) & mask) == (target & mask)) return true;
            }
        return false;
    }

    private static uint ToUInt(IPAddress v4)
    {
        var b = v4.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    /// <summary>"24-B2-B9-2C-55-05", as Windows writes it; empty when there is none.</summary>
    public static string FormatMac(byte[] bytes) => bytes.Length == 0 ? "" : BitConverter.ToString(bytes);

    /// <summary>"255.255.255.0" for 24.</summary>
    public static string SubnetMask(int prefixLength)
    {
        uint mask = prefixLength <= 0 ? 0 : prefixLength >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefixLength);
        return $"{mask >> 24}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
    }

    /// <summary>"100 Mbps", "1.2 Gbps".</summary>
    public static string BitRate(double bitsPerSecond, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double mbps = Math.Max(0, bitsPerSecond) / 1_000_000;
        return mbps >= 1000 ? (mbps / 1000).ToString("0.#", culture) + " Gbps" : mbps.ToString(mbps < 10 ? "0.#" : "0", culture) + " Mbps";
    }

    /// <summary>The band people know a Wi-Fi channel by: "2.4 GHz", "5 GHz", "6 GHz"; empty when unknown.</summary>
    public static string Band(int? frequencyMhz, int? channel)
    {
        if (frequencyMhz is int f)
            return f is >= 2400 and < 2500 ? "2.4 GHz" : f is >= 4900 and < 5925 ? "5 GHz" : f is >= 5925 and < 7125 ? "6 GHz" : "";
        return channel is >= 1 and <= 14 ? "2.4 GHz" : channel is >= 32 and <= 177 ? "5 GHz" : "";
    }

    /// <summary>The Wi-Fi standard from the PHY type: "Wi-Fi 6 (802.11ax)".</summary>
    public static string Standard(int phyType) => phyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        9 => "802.11ad",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => "",
    };

    /// <summary>SSIDs are bytes, usually UTF-8; a name that isn't valid UTF-8 is shown as hex.</summary>
    internal static string DecodeSsid(byte[] ssid)
    {
        try { return new UTF8Encoding(false, true).GetString(ssid); }
        catch (DecoderFallbackException) { return Convert.ToHexString(ssid); }
    }

    /// <summary>MIB_IF_ROW2.InterfaceAndOperStatusFlags.HardwareInterface: a real card rather than a virtual adapter.</summary>
    internal static bool IsHardwareInterface(int index)
    {
        const int rowSize = 1352, indexOffset = 8, flagsOffset = 1152;
        IntPtr row = Marshal.AllocHGlobal(rowSize);
        try
        {
            for (int i = 0; i < rowSize; i += 8) Marshal.WriteInt64(row, i, 0);
            Marshal.WriteInt32(row, indexOffset, index);
            return GetIfEntry2(row) == 0 && (Marshal.ReadByte(row, flagsOffset) & 1) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(row);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfEntry2(IntPtr row);

    /// <summary>The Native Wifi API: what each Wi-Fi adapter is connected to.</summary>
    private static class Wlan
    {
        private const int OpcodeCurrentConnection = 7, OpcodeChannelNumber = 8;
        private const int InterfaceInfoSize = 532; // GUID, WCHAR[256] description, state

        public static Dictionary<Guid, WifiConnection> CurrentConnections()
        {
            var result = new Dictionary<Guid, WifiConnection>();
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr client) != 0) return result;
            try
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr list) != 0) return result;
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count; i++)
                    {
                        var guid = Marshal.PtrToStructure<Guid>(list + 8 + i * InterfaceInfoSize);
                        if (Connection(client, guid) is { } connection) result[guid] = connection;
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(client, IntPtr.Zero); }
            return result;
        }

        private static WifiConnection? Connection(IntPtr client, Guid guid)
        {
            // WLAN_CONNECTION_ATTRIBUTES: state, mode, WCHAR[256] profile name, then the association:
            // SSID (length + 32 bytes) at 520, BSS type at 556, BSSID at 560, PHY type at 568,
            // PHY index, signal quality at 576, receive and transmit rates (kbps) at 580 and 584.
            if (WlanQueryInterface(client, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out int size, out IntPtr data, IntPtr.Zero) != 0)
                return null;
            WifiConnection connection;
            try
            {
                if (size < 588) return null;
                int ssidLength = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
                var ssid = new byte[ssidLength];
                Marshal.Copy(data + 524, ssid, 0, ssidLength);
                var bssid = new byte[6];
                Marshal.Copy(data + 560, bssid, 0, 6);
                connection = new WifiConnection
                {
                    Ssid = DecodeSsid(ssid),
                    Bssid = BitConverter.ToString(bssid),
                    PhyType = Marshal.ReadInt32(data, 568),
                    SignalPercent = Math.Clamp(Marshal.ReadInt32(data, 576), 0, 100),
                    ReceiveMbps = (uint)Marshal.ReadInt32(data, 580) / 1000.0,
                    TransmitMbps = (uint)Marshal.ReadInt32(data, 584) / 1000.0,
                };
            }
            finally { WlanFreeMemory(data); }

            int? channel = null;
            if (WlanQueryInterface(client, ref guid, OpcodeChannelNumber, IntPtr.Zero, out _, out IntPtr channelData, IntPtr.Zero) == 0)
            {
                channel = Marshal.ReadInt32(channelData, 0);
                WlanFreeMemory(channelData);
            }
            var (frequency, rssi) = Access(client, guid, connection.Bssid);
            return connection with { Channel = channel, FrequencyMhz = frequency, RssiDbm = rssi };
        }

        /// <summary>Frequency and signal (dBm) of the access point in use, from the cached scan list.</summary>
        private static (int? FrequencyMhz, int? RssiDbm) Access(IntPtr client, Guid guid, string bssid)
        {
            const int entrySize = 360, bssidOffset = 40, rssiOffset = 56, frequencyOffset = 92;
            if (WlanGetNetworkBssList(client, ref guid, IntPtr.Zero, 1 /* infrastructure */, false, IntPtr.Zero, out IntPtr list) != 0)
                return (null, null);
            try
            {
                int count = Marshal.ReadInt32(list, 4);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = list + 8 + i * entrySize;
                    var bytes = new byte[6];
                    Marshal.Copy(entry + bssidOffset, bytes, 0, 6);
                    if (BitConverter.ToString(bytes) != bssid) continue;
                    int kilohertz = Marshal.ReadInt32(entry, frequencyOffset);
                    return (kilohertz > 0 ? kilohertz / 1000 : null, Marshal.ReadInt32(entry, rssiOffset));
                }
                return (null, null);
            }
            finally { WlanFreeMemory(list); }
        }

        [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr client);
        [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr client, ref Guid guid, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] private static extern int WlanGetNetworkBssList(IntPtr client, ref Guid guid, IntPtr ssid, int bssType, bool securityEnabled, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    }
}
