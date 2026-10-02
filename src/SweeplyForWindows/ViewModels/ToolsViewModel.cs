using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Media;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>What a failed echo means, in a few words.</summary>
internal static class EchoText
{
    public static string Status(IPStatus status) => status switch
    {
        IPStatus.TimedOut => Loc.Instance["tools.status.TimedOut"],
        IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable
            or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable => Loc.Instance["tools.status.Unreachable"],
        _ => Loc.Instance.Format("tools.status.Other", status),
    };

    public static string Milliseconds(double ms, CultureInfo culture) => Loc.Instance.Format("tools.ms", ms.ToString("0", culture));
}

/// <summary>Ping on the Tools tab: once a second until stopped, with totals, a chart and the last answers.</summary>
public sealed class PingToolViewModel : ObservableObject
{
    private const int LinesKept = 8;
    private static readonly Brush ChartBrush = Frozen(Color.FromRgb(0x06, 0xB6, 0xD4));
    private readonly MetricHistory _history = new(MonitorViewModel.Window);
    private CancellationTokenSource? _run;
    private PingStats _stats = new();
    private string _target = "", _statusText = "", _statsText = "", _timesText = "", _scaleText = "";
    private IReadOnlyList<ChartPoint>? _points;
    private double _maximum = 10;
    private bool _isRunning;

    public PingToolViewModel() => StartStopCommand = new RelayCommand(_ => { if (IsRunning) Stop(); else _ = RunAsync(); });

    public string Target { get => _target; set => SetField(ref _target, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string StatsText { get => _statsText; private set => SetField(ref _statsText, value); }
    public string TimesText { get => _timesText; private set => SetField(ref _timesText, value); }
    public string ScaleText { get => _scaleText; private set => SetField(ref _scaleText, value); }
    public IReadOnlyList<ChartPoint>? Points { get => _points; private set => SetField(ref _points, value); }
    public double Maximum { get => _maximum; private set => SetField(ref _maximum, value); }
    public double WindowSeconds => MonitorViewModel.Window.TotalSeconds;
    public Brush Brush => ChartBrush;

    /// <summary>The last answers, newest first.</summary>
    public ObservableCollection<string> Lines { get; } = new();

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ButtonText));
        }
    }

    public bool CanEdit => !IsRunning;
    public string ButtonText => Loc.Instance[IsRunning ? "tools.stop" : "tools.start"];
    public ICommand StartStopCommand { get; }

    public void Stop()
    {
        _run?.Cancel();
        _run = null;
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(ButtonText));
        ShowTotals();
    }

    private async Task RunAsync()
    {
        var cancel = new CancellationTokenSource();
        _run = cancel;
        IsRunning = true;
        _stats = new PingStats();
        _history.Clear();
        Lines.Clear();
        Points = null;
        StatsText = TimesText = ScaleText = "";
        StatusText = "";
        string typed = Target.Trim();
        try
        {
            var address = await NetworkTools.ResolveAsync(typed, cancel.Token);
            if (address is null)
            {
                StatusText = Loc.Instance.Format("tools.resolveFailed", typed);
                return;
            }
            StatusText = NetworkTools.IsProxyFakeAddress(address) ? Loc.Instance.Format("tools.fakeAddress", address)
                : typed != address.ToString() ? Loc.Instance.Format("tools.resolved", typed, address) : "";

            using var ping = new Ping();
            while (!cancel.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                var (ms, status, from) = await NetworkTools.PingAsync(ping, address);
                if (cancel.IsCancellationRequested) break;
                Record(ms, status, from ?? address, started);
                var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_run == cancel) _run = null;
            IsRunning = false;
        }
    }

    private void Record(long? ms, IPStatus status, IPAddress from, DateTime when)
    {
        var culture = Loc.Instance.Culture;
        _stats.Add(ms);
        string time = when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Lines.Insert(0, ms is long t
            ? Loc.Instance.Format("tools.ping.reply", time, from, EchoText.Milliseconds(t, culture))
            : Loc.Instance.Format("tools.ping.lost", time, EchoText.Status(status)));
        while (Lines.Count > LinesKept) Lines.RemoveAt(Lines.Count - 1);
        if (ms is long answered) _history.Add(when, answered); // a lost echo leaves a gap in the chart
        ShowTotals();
    }

    private void ShowTotals()
    {
        if (_stats.Sent == 0) return;
        var culture = Loc.Instance.Culture;
        StatsText = Loc.Instance.Format("tools.ping.stats", _stats.Sent, _stats.Received, _stats.LossPercent);
        TimesText = _stats.Received == 0 ? "" : Loc.Instance.Format("tools.ping.times",
            EchoText.Milliseconds(_stats.Min!.Value, culture), EchoText.Milliseconds(_stats.Average!.Value, culture), EchoText.Milliseconds(_stats.Max!.Value, culture));
        var now = DateTime.UtcNow;
        Maximum = ChartScale.Nice(Math.Max(10, _history.Max(now) * 1.15));
        ScaleText = EchoText.Milliseconds(Maximum, culture);
        Points = _history.Points(now);
    }

    /// <summary>Made-up pings for screenshots.</summary>
    public void LoadSample(DateTime now)
    {
        Target = "192.0.2.1";
        var random = new Random(5);
        for (int i = 118; i >= 1; i--)
        {
            long? ms = i is 23 or 41 or 87 ? null : 2 + (long)(random.NextDouble() * (i is > 10 and < 16 ? 30 : 6));
            _stats.Add(ms);
            if (ms is long t) _history.Add(now.AddSeconds(-i), t);
            if (i <= 4)
                Lines.Insert(0, Loc.Instance.Format("tools.ping.reply", now.AddSeconds(-i).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Target,
                    EchoText.Milliseconds(ms ?? 3, Loc.Instance.Culture)));
        }
        var culture = Loc.Instance.Culture;
        StatsText = Loc.Instance.Format("tools.ping.stats", _stats.Sent, _stats.Received, _stats.LossPercent);
        TimesText = Loc.Instance.Format("tools.ping.times", EchoText.Milliseconds(_stats.Min!.Value, culture),
            EchoText.Milliseconds(_stats.Average!.Value, culture), EchoText.Milliseconds(_stats.Max!.Value, culture));
        Maximum = ChartScale.Nice(Math.Max(10, _history.Max(now) * 1.15));
        ScaleText = EchoText.Milliseconds(Maximum, culture);
        Points = _history.Points(now);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>One hop of a trace: its number, who answered, the name a DNS server gives for it, and how fast.</summary>
public sealed class TraceHopRow : ObservableObject
{
    private string _name = "";

    public TraceHopRow(TraceHop hop, CultureInfo culture)
    {
        Number = hop.Number.ToString(culture);
        Address = hop.Address?.ToString() ?? "*";
        TimeText = hop.Milliseconds is long ms ? EchoText.Milliseconds(ms, culture) : Loc.Instance["tools.trace.silent"];
        IsTarget = hop.IsTarget;
    }

    public string Number { get; }
    public string Address { get; }
    public string TimeText { get; }
    public bool IsTarget { get; }
    public string Name { get => _name; set => SetField(ref _name, value); }
}

/// <summary>Trace route on the Tools tab.</summary>
public sealed class TraceToolViewModel : ObservableObject
{
    private CancellationTokenSource? _run;
    private string _target = "", _statusText = "";
    private bool _isRunning;

    public TraceToolViewModel() => StartStopCommand = new RelayCommand(_ => { if (IsRunning) Stop(); else _ = RunAsync(); });

    public string Target { get => _target; set => SetField(ref _target, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public ObservableCollection<TraceHopRow> Hops { get; } = new();

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ButtonText));
        }
    }

    public bool CanEdit => !IsRunning;
    public string ButtonText => Loc.Instance[IsRunning ? "tools.stop" : "tools.start"];
    public ICommand StartStopCommand { get; }

    public void Stop()
    {
        _run?.Cancel();
        _run = null;
    }

    public void Relocalize() => OnPropertyChanged(nameof(ButtonText));

    private async Task RunAsync()
    {
        var cancel = new CancellationTokenSource();
        _run = cancel;
        IsRunning = true;
        Hops.Clear();
        string typed = Target.Trim();
        var culture = Loc.Instance.Culture;
        try
        {
            StatusText = "";
            var address = await NetworkTools.ResolveAsync(typed, cancel.Token);
            if (address is null)
            {
                StatusText = Loc.Instance.Format("tools.resolveFailed", typed);
                return;
            }
            string prefix = NetworkTools.IsProxyFakeAddress(address) ? Loc.Instance.Format("tools.fakeAddress", address) + " " : "";
            StatusText = prefix + Loc.Instance.Format("tools.trace.running", 1);
            var progress = new Progress<TraceHop>(hop =>
            {
                if (cancel.IsCancellationRequested) return;
                var row = new TraceHopRow(hop, culture);
                Hops.Add(row);
                StatusText = prefix + Loc.Instance.Format("tools.trace.running", hop.Number + 1);
                if (hop.Address is { } a) _ = NameAsync(row, a, cancel.Token);
            });
            bool reached = await NetworkTools.TraceAsync(address, progress, cancel.Token);
            StatusText = prefix + (reached ? Loc.Instance.Format("tools.trace.reached", Hops.Count) : Loc.Instance.Format("tools.trace.notReached", NetworkTools.MaxHops));
            // One hop to an address beyond the local network: something on this PC (a proxy in TUN mode,
            // a VPN) answered for it, and the real route can't be seen.
            if (reached && Hops.Count == 1 && !IPAddress.IsLoopback(address) && string.IsNullOrEmpty(prefix)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !NetworkAdapters.IsOnLocalNetwork(address, await Task.Run(NetworkAdapters.Read)))
                StatusText += " " + Loc.Instance["tools.trace.intercepted"];
        }
        catch (OperationCanceledException) { StatusText = Loc.Instance["tools.stopped"]; }
        finally
        {
            if (_run == cancel) _run = null;
            IsRunning = false;
        }
    }

    private static async Task NameAsync(TraceHopRow row, IPAddress address, CancellationToken cancel)
    {
        if (await NetworkTools.NameOfAsync(address, cancel) is { } name) row.Name = name;
    }

    /// <summary>Made-up hops for screenshots (documentation addresses).</summary>
    public void LoadSample()
    {
        Target = "www.example.com";
        var culture = Loc.Instance.Culture;
        foreach (var (n, address, ms, name) in new (int, string?, long?, string)[]
                 {
                     (1, "192.0.2.1", 2, "router.home.example"), (2, "198.51.100.1", 6, ""), (3, null, null, ""),
                     (4, "203.0.113.9", 11, "edge1.isp.example"), (5, "203.0.113.77", 14, ""), (6, "198.51.100.200", 15, "www.example.com"),
                 })
            Hops.Add(new TraceHopRow(new TraceHop(n, address is null ? null : IPAddress.Parse(address), ms, n == 6), culture) { Name = name });
        StatusText = Loc.Instance.Format("tools.trace.reached", 6);
    }
}

/// <summary>A network adapter that can be scanned, as the list shows it: "Wi-Fi · 192.0.2.23/24".</summary>
public sealed record ScanChoice(NetworkAdapter Adapter, IPAddress Address, int PrefixLength)
{
    public string Text => $"{Adapter.Name} · {Address}/{PrefixLength}";
}

/// <summary>One device found by the network scan.</summary>
public sealed record LanDeviceRow(string Address, string Name, string Mac, string TimeText, string Note);

/// <summary>The network scan on the Tools tab: the devices on one adapter's network.</summary>
public sealed class LanScanViewModel : ObservableObject
{
    private CancellationTokenSource? _run;
    private ScanChoice? _selected;
    private string _statusText = "", _progressText = "";
    private double _progress;
    private bool _isRunning;
    private IReadOnlyList<LanDevice> _found = Array.Empty<LanDevice>();
    private TimeSpan _took;

    public LanScanViewModel()
    {
        StartStopCommand = new RelayCommand(_ => { if (IsRunning) Stop(); else _ = RunAsync(); }, () => IsRunning || Selected is not null);
        CopyCommand = new RelayCommand(_ => Copy(), () => Devices.Count > 0);
    }

    public ObservableCollection<ScanChoice> Choices { get; } = new();

    public ScanChoice? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(RangeText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>"Scans 192.0.2.1 to 192.0.2.254 (254 addresses)." and, for big networks, that only part is scanned.</summary>
    public string RangeText
    {
        get
        {
            if (Selected is not { } s) return "";
            var range = LanScanner.Range(s.Address, s.PrefixLength);
            if (range.Count == 0) return Loc.Instance["tools.scan.nothing"];
            string text = Loc.Instance.Format("tools.scan.range", range.FirstAddress, range.LastAddress, range.Count);
            return range.Trimmed ? text + " " + Loc.Instance["tools.scan.trimmed"] : text;
        }
    }

    public ObservableCollection<LanDeviceRow> Devices { get; } = new();
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string ProgressText { get => _progressText; private set => SetField(ref _progressText, value); }
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }
    public bool HasDevices => Devices.Count > 0;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ButtonText));
        }
    }

    public bool CanEdit => !IsRunning;
    public string ButtonText => Loc.Instance[IsRunning ? "tools.stop" : "tools.scan.start"];
    public ICommand StartStopCommand { get; }
    public ICommand CopyCommand { get; }

    /// <summary>Connected adapters with an IPv4 address; real cards first.</summary>
    public void SetAdapters(IReadOnlyList<NetworkAdapter> adapters)
    {
        string? previous = Selected?.Adapter.Id;
        Choices.Clear();
        foreach (var a in adapters.Where(a => a.IsUp && a.InterfaceIndex > 0))
            foreach (var (address, prefix) in a.IPv4.Where(v => !IPAddress.IsLoopback(v.Address) && v.PrefixLength is > 0 and < 31))
                Choices.Add(new ScanChoice(a, address, prefix));
        Selected = Choices.FirstOrDefault(c => c.Adapter.Id == previous) ?? Choices.FirstOrDefault();
        if (Choices.Count == 0) StatusText = Loc.Instance["tools.scan.none"];
    }

    public void Stop()
    {
        _run?.Cancel();
        _run = null;
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(RangeText));
        Show();
    }

    private async Task RunAsync()
    {
        if (Selected is not { } choice) return;
        var cancel = new CancellationTokenSource();
        _run = cancel;
        IsRunning = true;
        Devices.Clear();
        OnPropertyChanged(nameof(HasDevices));
        StatusText = "";
        Progress = 0;
        var started = DateTime.UtcNow;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (cancel.IsCancellationRequested) return;
            Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            ProgressText = Loc.Instance.Format("tools.scan.progress", p.Done, p.Total);
        });
        try
        {
            _found = await LanScanner.ScanAsync(choice.Address, choice.PrefixLength, choice.Adapter.InterfaceIndex, choice.Adapter.MacAddress,
                choice.Adapter.Gateways, progress, cancel.Token);
            _took = DateTime.UtcNow - started;
            Show();
        }
        catch (OperationCanceledException) { StatusText = Loc.Instance["tools.stopped"]; }
        finally
        {
            if (_run == cancel) _run = null;
            IsRunning = false;
            ProgressText = "";
        }
    }

    private void Show()
    {
        if (_found.Count == 0) return;
        var loc = Loc.Instance;
        var culture = loc.Culture;
        Devices.Clear();
        foreach (var d in _found)
        {
            var notes = new List<string>();
            if (d.IsSelf) notes.Add(loc["tools.scan.self"]);
            if (d.IsGateway) notes.Add(loc["tools.scan.gateway"]);
            if (d.RandomMac) notes.Add(loc["tools.scan.random"]);
            string time = d.IsSelf ? "" : d.PingMs is long ms ? EchoText.Milliseconds(ms, culture) : loc["tools.scan.noPing"];
            Devices.Add(new LanDeviceRow(d.Address.ToString(), d.Name, d.Mac, time, string.Join(loc["hw.listSeparator"], notes)));
        }
        StatusText = loc.Format("tools.scan.summary", _found.Count, Math.Max(1, (int)Math.Round(_took.TotalSeconds)));
        OnPropertyChanged(nameof(HasDevices));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Copy()
    {
        var lines = Devices.Select(d => string.Join("\t", new[] { d.Address, d.Name, d.Mac, d.TimeText, d.Note }));
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy
    }

    /// <summary>Made-up devices for screenshots: documentation addresses and MACs (RFC 5737, RFC 7042).</summary>
    public void LoadSample()
    {
        var wifi = new NetworkAdapter
        {
            Id = "wifi", Name = "Wi-Fi", IsUp = true, IsHardware = true, InterfaceIndex = 12, MacAddress = "00-00-5E-00-53-2A",
            IPv4 = new[] { (IPAddress.Parse("192.0.2.23"), 24) }, Gateways = new[] { IPAddress.Parse("192.0.2.1") },
        };
        SetAdapters(new[] { wifi });
        _found = new[]
        {
            new LanDevice { Address = IPAddress.Parse("192.0.2.1"), Mac = "00-00-5E-00-53-01", PingMs = 2, Name = "router.home.example", IsGateway = true },
            new LanDevice { Address = IPAddress.Parse("192.0.2.23"), Mac = "00-00-5E-00-53-2A", Name = "ALEX-LAPTOP", IsSelf = true },
            new LanDevice { Address = IPAddress.Parse("192.0.2.31"), Mac = "00-00-5E-00-53-31", PingMs = 4, Name = "OFFICE-PRINTER" },
            new LanDevice { Address = IPAddress.Parse("192.0.2.47"), Mac = "02-00-5E-00-53-47", PingMs = 38 },
            new LanDevice { Address = IPAddress.Parse("192.0.2.58"), Mac = "00-00-5E-00-53-58", Name = "nas.home.example" },
            new LanDevice { Address = IPAddress.Parse("192.0.2.64"), Mac = "02-00-5E-00-53-64", PingMs = 61 },
        };
        _took = TimeSpan.FromSeconds(6);
        Show();
    }
}

/// <summary>The Monitor page's Tools tab: ping, trace route and the network scan.</summary>
public sealed class ToolsViewModel : ObservableObject
{
    private bool _suggested;

    public PingToolViewModel Ping { get; } = new();
    public TraceToolViewModel Trace { get; } = new();
    public LanScanViewModel Scan { get; } = new();

    /// <summary>The tab came into view: offer the router as the first thing to ping, and list the adapters to scan.</summary>
    public void SetShown(bool shown)
    {
        if (!shown) return;
        _ = LoadAdaptersAsync(suggest: !_suggested);
        _suggested = true;
    }

    /// <summary>The page went out of view (another page, window closed or minimized): stop sending.</summary>
    public void StopAll()
    {
        Ping.Stop();
        Trace.Stop();
        Scan.Stop();
    }

    public void Relocalize()
    {
        Ping.Relocalize();
        Trace.Relocalize();
        Scan.Relocalize();
    }

    private async Task LoadAdaptersAsync(bool suggest)
    {
        try
        {
            var adapters = await Task.Run(NetworkAdapters.Read);
            if (!Scan.IsRunning) Scan.SetAdapters(adapters);
            if (!suggest) return;
            var gateway = adapters.Where(a => a.IsUp && a.IsHardware).SelectMany(a => a.Gateways)
                .FirstOrDefault(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (gateway is not null && Ping.Target.Length == 0) Ping.Target = gateway.ToString();
        }
        catch (Exception) { } // suggestions only
    }

    public void LoadSample(DateTime now)
    {
        _suggested = true;
        Ping.LoadSample(now);
        Trace.LoadSample();
        Scan.LoadSample();
    }
}
