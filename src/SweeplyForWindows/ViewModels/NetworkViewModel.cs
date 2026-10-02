using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Sweeply.Core;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>One network adapter on the Network tab.</summary>
public sealed record AdapterCard(string Name, string Description, string Glyph, string StatusText, bool IsUp, IReadOnlyList<InfoPair> Pairs, ICommand CopyCommand);

/// <summary>
/// The Monitor page's Network tab: every adapter with its addresses, and Wi-Fi details. Read when the
/// tab comes into view, every 10 seconds while it stays, and right away when Windows says the network
/// changed. Only reads what Windows knows; nothing is sent.
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);
    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IReadOnlyList<NetworkAdapter> _adapters = Array.Empty<NetworkAdapter>();
    private bool _shown, _reading, _sampleMode;
    private string _copiedText = "";

    public NetworkViewModel()
    {
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); CopiedText = ""; };
        CopyCommand = new RelayCommand(p => Copy(p as string));
        NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkChanged();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => OnNetworkChanged();
    }

    /// <summary>Real cards (connected or not).</summary>
    public ObservableCollection<AdapterCard> Adapters { get; } = new();

    /// <summary>Virtual adapters: VMware, VPN tunnels, Bluetooth, Wi-Fi Direct… Folded away by default.</summary>
    public ObservableCollection<AdapterCard> OtherAdapters { get; } = new();

    public string OthersHeader => Loc.Instance.Format("net.others", OtherAdapters.Count);
    public bool HasOthers => OtherAdapters.Count > 0;

    /// <summary>"Copied: …" for a few seconds after a value was clicked.</summary>
    public string CopiedText { get => _copiedText; private set => SetField(ref _copiedText, value); }

    public ICommand CopyCommand { get; }

    public void SetShown(bool shown)
    {
        _shown = shown;
        if (_sampleMode) return;
        if (!shown)
        {
            _timer.Stop();
            return;
        }
        _ = RefreshAsync();
        _timer.Start();
    }

    public void Relocalize()
    {
        Show();
        OnPropertyChanged(nameof(OthersHeader));
    }

    private void OnNetworkChanged()
    {
        // Raised on a worker thread, often several times in a row; the read itself is cheap.
        Application.Current?.Dispatcher.BeginInvoke(() => { if (_shown && !_sampleMode) _ = RefreshAsync(); });
    }

    private async Task RefreshAsync()
    {
        if (_reading) return;
        _reading = true;
        try { _adapters = await Task.Run(NetworkAdapters.Read); }
        catch (Exception) { return; } // keep what is shown
        finally { _reading = false; }
        Show();
    }

    private void Show()
    {
        Adapters.Clear();
        OtherAdapters.Clear();
        foreach (var adapter in _adapters)
            (adapter.IsHardware ? Adapters : OtherAdapters).Add(Card(adapter, CopyCommand));
        OnPropertyChanged(nameof(OthersHeader));
        OnPropertyChanged(nameof(HasOthers));
    }

    private static AdapterCard Card(NetworkAdapter a, ICommand copy)
    {
        var loc = Loc.Instance;
        var culture = loc.Culture;
        var pairs = new List<InfoPair>();
        void Add(string key, string value) { if (value.Length > 0) pairs.Add(new InfoPair(loc[key], value)); }
        static string Lines(IEnumerable<string> lines) => string.Join("\n", lines);

        if (a.Wifi is { } w)
        {
            Add("net.ssid", w.Ssid.Length > 0 ? w.Ssid : loc["net.ssidHidden"]);
            Add("net.signal", w.RssiDbm is int dbm ? loc.Format("net.signalDbm", w.SignalPercent, dbm) : $"{w.SignalPercent}%");
            string band = NetworkAdapters.Band(w.FrequencyMhz, w.Channel);
            Add("net.band", string.Join(" · ", new[] { band, w.Channel is int ch ? loc.Format("net.channelValue", ch) : "" }.Where(t => t.Length > 0)));
            Add("net.standard", NetworkAdapters.Standard(w.PhyType));
            Add("net.linkRate", loc.Format("net.rxTx", NetworkAdapters.BitRate(w.ReceiveMbps * 1_000_000, culture), NetworkAdapters.BitRate(w.TransmitMbps * 1_000_000, culture)));
        }
        else if (a.IsUp && a.SpeedBitsPerSecond > 0)
        {
            Add("net.speed", NetworkAdapters.BitRate(a.SpeedBitsPerSecond, culture));
        }
        Add("net.ipv4", Lines(a.IPv4.Select(v => $"{v.Address}/{v.PrefixLength}")));
        if (a.IPv4.Count > 0) Add("net.mask", NetworkAdapters.SubnetMask(a.IPv4[0].PrefixLength));
        Add("net.gateway", Lines(a.Gateways.Select(g => g.ToString())));
        Add("net.dns", Lines(a.DnsServers.Select(d => d.ToString())));
        Add("net.ipv6", Lines(a.IPv6.Select(v => v.ToString())));
        Add("net.mac", a.MacAddress);
        if (a.IsUp && a.IPv4.Count > 0)
            Add("net.dhcp", !a.DhcpEnabled ? loc["net.dhcpOff"]
                : a.DhcpServers.Count > 0 ? loc.Format("net.dhcpOn", string.Join(", ", a.DhcpServers)) : loc["net.dhcpOnShort"]);
        if (a.IsUp)
            Add("net.traffic", loc.Format("net.rxTx", SizeFormatter.Format(a.BytesReceived, culture), SizeFormatter.Format(a.BytesSent, culture)));

        string glyph = a.IsWifi ? "" : a.IsHardware ? "" : "";
        return new AdapterCard(a.Name, a.Description, glyph, loc[a.IsUp ? "net.connected" : "net.disconnected"], a.IsUp, pairs, copy);
    }

    private void Copy(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try { Clipboard.SetText(value.Replace("\n", Environment.NewLine)); }
        catch (System.Runtime.InteropServices.ExternalException) { return; } // the clipboard is busy
        CopiedText = Loc.Instance.Format("net.copied", value.Replace("\n", ", "));
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    /// <summary>Made-up adapters for screenshots, with documentation addresses (RFC 5737 / 3849 / 7042).</summary>
    public void LoadSample()
    {
        _sampleMode = true;
        IPAddress Ip(string s) => IPAddress.Parse(s);
        _adapters = new[]
        {
            new NetworkAdapter
            {
                Id = "wifi", Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX211 160MHz", IsWifi = true, IsUp = true, IsHardware = true,
                SpeedBitsPerSecond = 1_201_000_000, MacAddress = "00-00-5E-00-53-2A",
                IPv4 = new[] { (Ip("192.0.2.23"), 24) }, Gateways = new[] { Ip("192.0.2.1") }, DnsServers = new[] { Ip("192.0.2.1"), Ip("198.51.100.53") },
                IPv6 = new[] { Ip("2001:db8:1f70::9a3b"), Ip("fe80::5efe:c000:217") },
                DhcpEnabled = true, DhcpServers = new[] { Ip("192.0.2.1") },
                BytesReceived = 3_456_000_000, BytesSent = 412_000_000,
                Wifi = new WifiConnection { Ssid = "Coffee & Code", SignalPercent = 88, RssiDbm = -46, Channel = 149, FrequencyMhz = 5745, PhyType = 10, ReceiveMbps = 1201, TransmitMbps = 960.8 },
            },
            new NetworkAdapter { Id = "eth", Name = "Ethernet", Description = "Realtek PCIe GbE Family Controller", IsHardware = true, MacAddress = "00-00-5E-00-53-10" },
            new NetworkAdapter { Id = "vm", Name = "vEthernet (Default Switch)", Description = "Hyper-V Virtual Ethernet Adapter", IsUp = true, MacAddress = "00-00-5E-00-53-77", IPv4 = new[] { (Ip("203.0.113.1"), 28) } },
        };
        Show();
    }
}
