using System.Globalization;
using System.Windows.Media;
using Sweeply.Core;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>One card of the Performance tab: a big number, a line of detail and a chart of the last minutes.</summary>
public sealed class MetricCard : ObservableObject
{
    private readonly string _titleKey;
    private string _value = "—", _detail = "", _legend1 = "", _legend2 = "", _scaleText = "";
    private IReadOnlyList<ChartPoint>? _points, _points2;
    private double _minimum, _maximum = 100;

    public MetricCard(string titleKey, string glyph, Color color, double maxGapSeconds = 5)
    {
        _titleKey = titleKey;
        Glyph = glyph;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brush = brush;
        MaxGapSeconds = maxGapSeconds;
    }

    public string Title => Loc.Instance[_titleKey];
    public string Glyph { get; }
    public Brush Brush { get; }
    public double MaxGapSeconds { get; }
    public double WindowSeconds => MonitorViewModel.Window.TotalSeconds;

    public string Value { get => _value; set => SetField(ref _value, value); }
    public string Detail { get => _detail; set => SetField(ref _detail, value); }

    /// <summary>Under the chart; with a second series, each line names its series.</summary>
    public string Legend1 { get => _legend1; set => SetField(ref _legend1, value); }
    public string Legend2 { get => _legend2; set => SetField(ref _legend2, value); }

    /// <summary>What the top of the chart stands for, e.g. "100%" or "5 MB/s".</summary>
    public string ScaleText { get => _scaleText; set => SetField(ref _scaleText, value); }

    public IReadOnlyList<ChartPoint>? Points { get => _points; set => SetField(ref _points, value); }
    public IReadOnlyList<ChartPoint>? Points2 { get => _points2; set => SetField(ref _points2, value); }
    public double Minimum { get => _minimum; set => SetField(ref _minimum, value); }
    public double Maximum { get => _maximum; set => SetField(ref _maximum, value); }

    public void Relocalize() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// The Monitor page. Samples arrive from the activity monitor's loops while the page is shown (or
/// while the icon number, icon details or floating bar need them); the page only keeps the last
/// minutes in memory for its charts.
/// </summary>
public sealed class MonitorViewModel : ObservableObject
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    private const double DiskScaleMinimum = 1024 * 1024;     // a quiet disk still gets a 1 MB/s scale
    private const double NetworkScaleMinimum = 128 * 1024;   // and a quiet network 128 KB/s

    private readonly MetricHistory _cpu = new(Window), _memory = new(Window), _diskRead = new(Window), _diskWrite = new(Window),
        _download = new(Window), _upload = new(Window), _gpu = new(Window), _temperature = new(Window);
    private SystemSample? _last;
    private IReadOnlyList<ThermalReading> _thermals = Array.Empty<ThermalReading>();
    private bool _thermalsRead;
    private string _processorName = SystemInfo.ProcessorName();
    private int _logicalProcessors = SystemInfo.LogicalProcessors;
    private Dictionary<long, string> _gpuNames = new();
    private DateTime _gpuNamesUtc = DateTime.MinValue;
    private DateTime? _sampleNow; // screenshots: a fixed "now"
    private int _tabIndex;
    private bool _shown;

    public MonitorViewModel()
    {
        Cards = new[] { Cpu, Memory, Disk, Network, Gpu, Temperature };
        Refresh(Now);
    }

    public MetricCard Cpu { get; } = new("monitor.card.cpu", "", Color.FromRgb(0x3B, 0x82, 0xF6));
    public MetricCard Memory { get; } = new("monitor.card.memory", "", Color.FromRgb(0xA8, 0x55, 0xF7));
    public MetricCard Disk { get; } = new("monitor.card.disk", "", Color.FromRgb(0x10, 0xB9, 0x81));
    public MetricCard Network { get; } = new("monitor.card.network", "", Color.FromRgb(0x06, 0xB6, 0xD4));
    public MetricCard Gpu { get; } = new("monitor.card.gpu", "", Color.FromRgb(0xEC, 0x48, 0x99));
    // Temperatures come every 2 seconds while the page is shown, every 10 otherwise.
    public MetricCard Temperature { get; } = new("monitor.card.temperature", "", Color.FromRgb(0xF9, 0x73, 0x16), maxGapSeconds: 25);

    /// <summary>The Performance tab's cards, in order.</summary>
    public IReadOnlyList<MetricCard> Cards { get; }

    /// <summary>The Hardware tab.</summary>
    public HardwareViewModel Hardware { get; } = new();

    /// <summary>The Network tab.</summary>
    public NetworkViewModel NetworkInfo { get; } = new();

    /// <summary>The Tools tab: ping and trace route.</summary>
    public ToolsViewModel Tools { get; } = new();

    /// <summary>The Connections tab: which program talks to where.</summary>
    public ConnectionsViewModel Connections { get; } = new();

    /// <summary>Tab numbers, in the order of the tabs.</summary>
    public const int PerformanceTab = 0, HardwareTab = 1, NetworkTab = 2, ToolsTab = 3, ConnectionsTab = 4;

    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (SetField(ref _tabIndex, value)) TellTabs();
        }
    }

    /// <summary>Only the tab in view reads what it shows.</summary>
    private void TellTabs()
    {
        Hardware.SetShown(_shown && TabIndex == HardwareTab);
        NetworkInfo.SetShown(_shown && TabIndex == NetworkTab);
        Tools.SetShown(_shown && TabIndex == ToolsTab);
        Connections.SetShown(_shown && TabIndex == ConnectionsTab);
    }

    private DateTime Now => _sampleNow ?? DateTime.UtcNow;

    /// <summary>A new second of activity (on the UI thread).</summary>
    public void OnSample(SystemSample sample)
    {
        var now = Now;
        _cpu.Add(now, sample.CpuPercent);
        if (sample.MemoryTotalBytes > 0) _memory.Add(now, 100.0 * sample.MemoryUsedBytes / sample.MemoryTotalBytes);
        _diskRead.Add(now, sample.DiskReadBytesPerSecond);
        _diskWrite.Add(now, sample.DiskWriteBytesPerSecond);
        _download.Add(now, sample.DownloadBytesPerSecond);
        _upload.Add(now, sample.UploadBytesPerSecond);
        if (sample.Gpu is { } gpu) _gpu.Add(now, gpu.Percent);
        _last = sample;
        Refresh(now);
    }

    /// <summary>New temperatures (on the UI thread).</summary>
    public void OnThermals(IReadOnlyList<ThermalReading> readings)
    {
        _thermals = readings;
        _thermalsRead = true;
        if (ThermalSampler.Hottest(readings) is { } hottest) _temperature.Add(Now, hottest.Celsius);
        Hardware.OnThermals(readings, Now);
        Refresh(Now);
    }

    /// <summary>
    /// The page came into view (window shown, not minimized, page selected) or went out of it. Coming
    /// into view moves the charts on to now, even before the next sample.
    /// </summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        if (shown) Refresh(Now);
        else Tools.StopAll(); // nothing keeps sending once the page is out of view
        TellTabs();
    }

    public void Relocalize()
    {
        foreach (var card in Cards) card.Relocalize();
        Hardware.Relocalize();
        NetworkInfo.Relocalize();
        Tools.Relocalize();
        Connections.Relocalize();
        Refresh(Now);
    }

    private void Refresh(DateTime now)
    {
        var culture = Loc.Instance.Culture;
        var loc = Loc.Instance;
        var s = _last;

        // CPU
        Cpu.Value = s is { } c1 ? RateFormatter.Percent(c1.CpuPercent, culture) + "%" : "—";
        Cpu.Detail = _processorName;
        Cpu.Legend1 = loc.Format("monitor.cpu.logical", _logicalProcessors) +
                      (s?.CpuGigahertz is double ghz ? " · " + loc.Format("monitor.cpu.speed", ghz.ToString("0.00", culture)) : "");
        Cpu.Points = _cpu.Points(now);
        Cpu.ScaleText = "100%";

        // Memory
        if (s is { MemoryTotalBytes: > 0 } m)
        {
            Memory.Value = RateFormatter.Percent(100.0 * m.MemoryUsedBytes / m.MemoryTotalBytes, culture) + "%";
            Memory.Detail = loc.Format("monitor.memory.detail", SizeFormatter.Format(m.MemoryUsedBytes, culture), SizeFormatter.Format(m.MemoryTotalBytes, culture));
            Memory.Legend1 = loc.Format("monitor.memory.available", SizeFormatter.Format(m.MemoryTotalBytes - m.MemoryUsedBytes, culture));
        }
        Memory.Points = _memory.Points(now);
        Memory.ScaleText = "100%";

        // Disk and network: two series each, on a round scale that fits the busiest moment shown.
        Disk.Detail = loc["monitor.disk.detail"];
        Network.Detail = loc["monitor.network.detail"];
        if (s is { } d)
        {
            Disk.Value = RateFormatter.Format(d.DiskReadBytesPerSecond + d.DiskWriteBytesPerSecond, culture);
            Disk.Legend1 = loc.Format("monitor.read", RateFormatter.Format(d.DiskReadBytesPerSecond, culture));
            Disk.Legend2 = loc.Format("monitor.write", RateFormatter.Format(d.DiskWriteBytesPerSecond, culture));
            Network.Value = RateFormatter.Format(d.DownloadBytesPerSecond + d.UploadBytesPerSecond, culture);
            Network.Legend1 = loc.Format("monitor.download", RateFormatter.Format(d.DownloadBytesPerSecond, culture));
            Network.Legend2 = loc.Format("monitor.upload", RateFormatter.Format(d.UploadBytesPerSecond, culture));
        }
        SetRateChart(Disk, _diskRead, _diskWrite, DiskScaleMinimum, now, culture);
        SetRateChart(Network, _download, _upload, NetworkScaleMinimum, now, culture);

        // Graphics card
        if (s?.Gpu is { } gpu)
        {
            Gpu.Value = RateFormatter.Percent(gpu.Percent, culture) + "%";
            Gpu.Detail = GpuName(gpu.AdapterLuid, now);
            Gpu.Legend1 = loc["monitor.gpu.note"];
        }
        else if (s is not null && _gpu.Count == 0)
        {
            Gpu.Value = "—";
            Gpu.Detail = "";
            Gpu.Legend1 = loc["monitor.gpu.none"];
        }
        Gpu.Points = _gpu.Points(now);
        Gpu.ScaleText = "100%";

        // Temperature: the hottest part, on a scale around what was seen.
        if (ThermalSampler.Hottest(_thermals) is { } hot)
        {
            Temperature.Value = RateFormatter.Celsius(hot.Celsius, culture);
            Temperature.Detail = loc.Format("monitor.temp.hottest", hot.Name);
            double low = _temperature.Min(now) ?? hot.Celsius, high = Math.Max(_temperature.Max(now), hot.Celsius);
            string lowText = RateFormatter.Celsius(low, culture), highText = RateFormatter.Celsius(high, culture);
            Temperature.Legend1 = lowText == highText
                ? loc.Format("monitor.temp.steady", highText)
                : loc.Format("monitor.temp.range", lowText, highText);
            Temperature.Minimum = Math.Floor((low - 3) / 5) * 5;
            Temperature.Maximum = Math.Ceiling((high + 3) / 5) * 5;
            Temperature.ScaleText = $"{Temperature.Minimum.ToString("0", culture)}–{RateFormatter.Celsius(Temperature.Maximum, culture)}";
        }
        else if (_thermalsRead)
        {
            Temperature.Value = "—";
            Temperature.Detail = "";
            Temperature.Legend1 = loc["monitor.temp.none"];
            Temperature.ScaleText = "";
        }
        Temperature.Points = _temperature.Points(now);
    }

    private static void SetRateChart(MetricCard card, MetricHistory first, MetricHistory second, double minimum, DateTime now, CultureInfo culture)
    {
        card.Points = first.Points(now);
        card.Points2 = second.Points(now);
        card.Maximum = ChartScale.Rate(Math.Max(first.Max(now), second.Max(now)), minimum);
        card.ScaleText = RateFormatter.Scale(card.Maximum, culture);
    }

    /// <summary>The card's name from Windows' list of graphics adapters, looked up again at most every 30 seconds.</summary>
    private string GpuName(long luid, DateTime now)
    {
        if (!_gpuNames.ContainsKey(luid) && _sampleNow is null && now - _gpuNamesUtc > TimeSpan.FromSeconds(30))
        {
            _gpuNamesUtc = now;
            try { _gpuNames = GraphicsAdapters.Names(); }
            catch (Exception) { } // the name is a nicety
        }
        return _gpuNames.GetValueOrDefault(luid, "");
    }

    /// <summary>Made-up activity for screenshots: nothing from this PC.</summary>
    public void LoadSample()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        _sampleNow = now;
        _processorName = "Intel(R) Core(TM) Ultra 7 155H";
        _logicalProcessors = 22;
        _gpuNames = new Dictionary<long, string> { [1] = "NVIDIA GeForce RTX 4060 Laptop GPU" };
        foreach (var h in new[] { _cpu, _memory, _diskRead, _diskWrite, _download, _upload, _gpu, _temperature }) h.Clear();

        var random = new Random(7);
        const double mb = 1024 * 1024;
        for (int i = 120; i >= 0; i--)
        {
            var t = now.AddSeconds(-i);
            double phase = (120 - i) / 120.0;
            _cpu.Add(t, Math.Clamp(22 + 14 * Math.Sin(phase * 9) + 8 * random.NextDouble() + (i is > 40 and < 55 ? 35 : 0), 0, 100));
            _memory.Add(t, 46 + 6 * phase + random.NextDouble());
            _diskRead.Add(t, (i % 23 < 3 ? 18 : 0.4) * mb * (0.6 + random.NextDouble()));
            _diskWrite.Add(t, (i % 31 < 4 ? 9 : 0.8) * mb * (0.6 + random.NextDouble()));
            _download.Add(t, (i is > 30 and < 70 ? 2.6 : 0.15) * mb * (0.7 + 0.6 * random.NextDouble()));
            _upload.Add(t, 0.05 * mb * (0.5 + random.NextDouble()));
            _gpu.Add(t, Math.Clamp(12 + 10 * Math.Sin(phase * 5 + 1) + 4 * random.NextDouble(), 0, 100));
            if (i % 2 == 0 && i > 0) _temperature.Add(t, 46 + 4 * phase + 1.5 * Math.Sin(phase * 7) + random.NextDouble());
        }
        _last = new SystemSample(37, 3.4 * mb, 1.2 * mb, 0.04 * mb)
        {
            DiskReadBytesPerSecond = 0.6 * mb,
            MemoryUsedBytes = (long)(16.2 * 1024 * mb),
            MemoryTotalBytes = 32L * 1024 * 1024 * 1024,
            CpuGigahertz = 3.81,
            Gpu = new GpuUsage(18, 1),
        };
        _thermals = new[]
        {
            new ThermalReading(ThermalPart.Disk, "Samsung SSD 990 PRO 1TB", 43.2),
            new ThermalReading(ThermalPart.Graphics, "NVIDIA GeForce RTX 4060 Laptop GPU", 51.0),
        };
        _thermalsRead = true;
        _temperature.Add(now, 51.0);
        Hardware.LoadSample(now);
        NetworkInfo.LoadSample();
        Tools.LoadSample(now);
        Connections.LoadSample();
        Refresh(now);
    }
}
