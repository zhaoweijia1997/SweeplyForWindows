using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sweeply.Core;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>A label and its value, e.g. "Memory" and "32.0 GB".</summary>
public sealed record InfoPair(string Label, string Value);

/// <summary>One part's temperature on the Hardware tab: now, the last minutes as a small chart, and its limit when known.</summary>
public sealed class ThermalRow : ObservableObject
{
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xF9, 0x73, 0x16));
    private string _title = "", _valueText = "", _rangeText = "", _limitText = "";
    private IReadOnlyList<ChartPoint>? _points;
    private double _minimum, _maximum = 100;

    public ThermalRow(ThermalPart part, string name)
    {
        Part = part;
        Name = name;
    }

    public ThermalPart Part { get; }
    public string Name { get; }

    /// <summary>What kind of part and which one, e.g. "Drive · Samsung SSD 990 PRO 1TB".</summary>
    public string Title { get => _title; set => SetField(ref _title, value); }

    /// <summary>"Drive · Samsung SSD 990 PRO 1TB": the kind of part in the current language, then its own (model) name.</summary>
    public static string Label(ThermalPart part, string name) => $"{Loc.Instance["monitor.part." + part]} · {name}";
    public string Glyph => Part == ThermalPart.Disk ? "" : "";
    public Brush Brush => Orange;
    public double WindowSeconds => MonitorViewModel.Window.TotalSeconds;
    internal MetricHistory History { get; } = new(MonitorViewModel.Window);

    public string ValueText { get => _valueText; set => SetField(ref _valueText, value); }
    public string RangeText { get => _rangeText; set => SetField(ref _rangeText, value); }
    public string LimitText { get => _limitText; set => SetField(ref _limitText, value); }
    public IReadOnlyList<ChartPoint>? Points { get => _points; set => SetField(ref _points, value); }
    public double Minimum { get => _minimum; set => SetField(ref _minimum, value); }
    public double Maximum { get => _maximum; set => SetField(ref _maximum, value); }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>An NVMe drive's health on the Hardware tab.</summary>
public sealed record DriveHealthCard(string Name, string StatusText, bool IsWarning, IReadOnlyList<InfoPair> Pairs);

/// <summary>
/// The Monitor page's Hardware tab: every part's temperature, NVMe drives' health and what the PC is
/// made of. Health is read when the tab comes into view and every minute while it stays; the
/// configuration once.
/// </summary>
public sealed class HardwareViewModel : ObservableObject
{
    private static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _healthTimer = new() { Interval = HealthEvery };
    private IReadOnlyList<DriveHealth> _health = Array.Empty<DriveHealth>();
    private HardwareSummary? _summary;
    private bool _summaryRequested, _healthRead, _thermalsRead, _sampleMode, _reading;

    public HardwareViewModel()
    {
        _healthTimer.Tick += (_, _) => _ = ReadHealthAsync();
        CopyConfigurationCommand = new RelayCommand(_ => CopyConfiguration(), () => Configuration.Count > 0);
    }

    public ObservableCollection<ThermalRow> Temperatures { get; } = new();
    public ObservableCollection<DriveHealthCard> Drives { get; } = new();
    public ObservableCollection<InfoPair> Configuration { get; } = new();
    public ICommand CopyConfigurationCommand { get; }

    /// <summary>Shown once the temperatures were read and none could be.</summary>
    public bool NoTemperatures => _thermalsRead && Temperatures.Count == 0;

    /// <summary>Shown once the drives were asked and none keeps a health log.</summary>
    public bool NoDriveHealth => _healthRead && Drives.Count == 0;

    /// <summary>The tab came into view or went out of it.</summary>
    public void SetShown(bool shown)
    {
        if (_sampleMode) return;
        if (!shown)
        {
            _healthTimer.Stop();
            return;
        }
        if (!_summaryRequested)
        {
            _summaryRequested = true;
            _ = ReadSummaryAsync();
        }
        _ = ReadHealthAsync();
        _healthTimer.Start();
    }

    /// <summary>New temperatures (on the UI thread).</summary>
    public void OnThermals(IReadOnlyList<ThermalReading> readings, DateTime now)
    {
        _thermalsRead = true;
        // Parts that stopped answering go; new ones come in, in the order they were read.
        foreach (var gone in Temperatures.Where(r => !readings.Any(t => t.Part == r.Part && t.Name == r.Name)).ToList())
            Temperatures.Remove(gone);
        for (int i = 0; i < readings.Count; i++)
        {
            var reading = readings[i];
            var row = Temperatures.FirstOrDefault(r => r.Part == reading.Part && r.Name == reading.Name);
            if (row is null) Temperatures.Insert(Math.Min(i, Temperatures.Count), row = new ThermalRow(reading.Part, reading.Name));
            row.History.Add(now, reading.Celsius);
            UpdateRow(row, reading.Celsius, now);
        }
        OnPropertyChanged(nameof(NoTemperatures));
    }

    public void Relocalize()
    {
        var now = DateTime.UtcNow;
        foreach (var row in Temperatures)
            if (row.History.Points(now) is { Length: > 0 } points) UpdateRow(row, points[^1].Value, now);
        ShowHealth();
        ShowSummary();
    }

    private void UpdateRow(ThermalRow row, double celsius, DateTime now)
    {
        var culture = Loc.Instance.Culture;
        double low = row.History.Min(now) ?? celsius, high = Math.Max(row.History.Max(now), celsius);
        string lowText = RateFormatter.Celsius(low, culture), highText = RateFormatter.Celsius(high, culture);
        row.Title = ThermalRow.Label(row.Part, row.Name);
        row.ValueText = RateFormatter.Celsius(celsius, culture);
        row.RangeText = lowText == highText ? Loc.Instance.Format("monitor.temp.steady", highText) : Loc.Instance.Format("monitor.temp.range", lowText, highText);
        row.Minimum = Math.Floor((low - 3) / 5) * 5;
        row.Maximum = Math.Ceiling((high + 3) / 5) * 5;
        row.Points = row.History.Points(now);
        var limit = row.Part == ThermalPart.Disk ? _health.FirstOrDefault(h => h.Name == row.Name)?.WarningCelsius : null;
        row.LimitText = limit is double l ? Loc.Instance.Format("hw.limit", RateFormatter.Celsius(l, culture)) : "";
    }

    private async Task ReadSummaryAsync()
    {
        try { _summary = await Task.Run(HardwareInfo.Read); }
        catch (Exception) { return; } // the configuration is a nicety
        ShowSummary();
    }

    private async Task ReadHealthAsync()
    {
        if (_reading) return;
        _reading = true;
        try { _health = await Task.Run(DriveHealthReader.ReadAll); }
        catch (Exception) { _health = Array.Empty<DriveHealth>(); }
        finally { _reading = false; }
        _healthRead = true;
        ShowHealth();
        foreach (var row in Temperatures) // the warning lines came with the health
            if (row.Points is { Count: > 0 } p) UpdateRow(row, p[^1].Value, DateTime.UtcNow);
    }

    private void ShowHealth()
    {
        var loc = Loc.Instance;
        var culture = loc.Culture;
        Drives.Clear();
        foreach (var h in _health)
        {
            var reasons = Enum.GetValues<DriveWarnings>().Where(w => w != DriveWarnings.None && h.Warnings.HasFlag(w)).Select(w => loc[$"hw.warn.{w}"]).ToList();
            string status = reasons.Count == 0 ? loc["hw.health.ok"] : loc.Format("hw.health.attention", string.Join(loc["hw.listSeparator"], reasons));
            var pairs = new List<InfoPair>
            {
                new(loc["hw.health.used"], $"{h.PercentUsed}%"),
                new(loc["hw.health.spare"], loc.Format("hw.health.spareValue", h.AvailableSpare, h.SpareThreshold)),
                new(loc["hw.health.written"], SizeFormatter.Format(h.BytesWritten, culture)),
                new(loc["hw.health.read"], SizeFormatter.Format(h.BytesRead, culture)),
                new(loc["hw.health.hours"], loc.Format("hw.health.hoursValue", h.PowerOnHours.ToString("N0", culture))),
                new(loc["hw.health.cycles"], loc.Format("hw.health.timesValue", h.PowerCycles.ToString("N0", culture))),
                new(loc["hw.health.unsafe"], loc.Format("hw.health.timesValue", h.UnsafeShutdowns.ToString("N0", culture))),
                new(loc["hw.health.errors"], h.MediaErrors.ToString("N0", culture)),
            };
            if (h.Celsius is double c)
                pairs.Insert(2, new(loc["monitor.card.temperature"], h.WarningCelsius is double w
                    ? loc.Format("hw.health.tempValue", RateFormatter.Celsius(c, culture), RateFormatter.Celsius(w, culture))
                    : RateFormatter.Celsius(c, culture)));
            Drives.Add(new DriveHealthCard(h.Name, status, reasons.Count > 0, pairs));
        }
        OnPropertyChanged(nameof(NoDriveHealth));
    }

    private void ShowSummary()
    {
        if (_summary is not { } s) return;
        var loc = Loc.Instance;
        var culture = loc.Culture;
        Configuration.Clear();
        void Add(string key, string value) { if (value.Length > 0) Configuration.Add(new InfoPair(loc[key], value)); }

        Add("hw.computer", s.ComputerModel);
        Add("hw.windows", s.Windows);
        var cpu = new List<string> { s.ProcessorName };
        if (s.Cores > 0)
            cpu.Add(s.PerformanceCores > 0
                ? loc.Format("hw.cores.hybrid", s.Cores, s.PerformanceCores, s.EfficientCores)
                : loc.Format("hw.cores", s.Cores));
        cpu.Add(loc.Format("hw.threads", s.LogicalProcessors));
        Add("hw.processor", string.Join(" · ", cpu.Where(t => t.Length > 0)));

        var memory = new StringBuilder(s.MemoryBytes > 0 ? SizeFormatter.Format(s.MemoryBytes, culture) : "");
        foreach (var group in s.MemoryModules.GroupBy(m => (m.Bytes, m.Type, m.SpeedMts, m.Manufacturer)))
        {
            var (bytes, type, speed, maker) = group.Key;
            var parts = new List<string> { $"{group.Count()} × {SizeFormatter.Format(bytes, culture)}" };
            if (type.Length > 0) parts.Add(type);
            if (speed > 0) parts.Add($"{speed} MT/s");
            if (maker.Length > 0) parts.Add(maker);
            memory.Append(memory.Length > 0 ? "\n" : "").Append(string.Join(" ", parts));
        }
        Add("hw.memory", memory.ToString());
        Add("hw.graphics", string.Join("\n", s.Graphics));
        Add("hw.drives", string.Join("\n", s.Drives.Select(d =>
            string.Join(" · ", new[] { d.Name, d.Bytes is long b ? SizeFormatter.Format(b, culture) : "", d.Bus }.Where(t => t.Length > 0)))));
        CommandManager.InvalidateRequerySuggested();
    }

    private void CopyConfiguration()
    {
        var text = string.Join(Environment.NewLine, Configuration.Select(p => $"{p.Label}: {p.Value.Replace("\n", "; ")}"));
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy; the user can try again
    }

    /// <summary>Made-up hardware for screenshots: nothing from this PC.</summary>
    public void LoadSample(DateTime now)
    {
        _sampleMode = true;
        _summary = new HardwareSummary
        {
            ComputerModel = "Contoso Laptop 15 (CL15-2026)",
            Windows = "Windows 11 Home 24H2 (26100.4061)",
            ProcessorName = "Intel(R) Core(TM) Ultra 7 155H",
            Cores = 16, PerformanceCores = 6, EfficientCores = 10, LogicalProcessors = 22,
            MemoryBytes = 32L << 30,
            MemoryModules = new[] { new MemoryModule(16L << 30, "LPDDR5", 7467, "Samsung", ""), new MemoryModule(16L << 30, "LPDDR5", 7467, "Samsung", "") },
            Graphics = new[] { "NVIDIA GeForce RTX 4060 Laptop GPU", "Intel(R) Arc(TM) Graphics" },
            Drives = new[] { new DriveSummary("Samsung SSD 990 PRO 1TB", 1_000_204_886_016, "NVMe") },
        };
        _health = new[]
        {
            new DriveHealth
            {
                Name = "Samsung SSD 990 PRO 1TB", Celsius = 43.2, WarningCelsius = 82, CriticalCelsius = 85,
                PercentUsed = 2, AvailableSpare = 100, SpareThreshold = 10, BytesRead = 18_300_000_000_000, BytesWritten = 12_700_000_000_000,
                PowerOnHours = 2_418, PowerCycles = 391, UnsafeShutdowns = 14, MediaErrors = 0,
            },
        };
        _healthRead = true;
        var random = new Random(3);
        Temperatures.Clear();
        foreach (var (part, name, base_) in new[] { (ThermalPart.Disk, "Samsung SSD 990 PRO 1TB", 41.0), (ThermalPart.Graphics, "NVIDIA GeForce RTX 4060 Laptop GPU", 48.0) })
        {
            var row = new ThermalRow(part, name);
            for (int i = 120; i >= 0; i -= 2)
                row.History.Add(now.AddSeconds(-i), base_ + 2.5 * Math.Sin((120 - i) / 19.0) + random.NextDouble());
            Temperatures.Add(row);
            UpdateRow(row, row.History.Points(now)[^1].Value, now);
        }
        _thermalsRead = true;
        ShowHealth();
        ShowSummary();
        OnPropertyChanged(nameof(NoTemperatures));
    }
}
