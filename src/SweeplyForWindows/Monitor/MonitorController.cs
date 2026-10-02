using System.Text;
using System.Windows;
using System.Windows.Threading;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;
using SweeplyForWindows.Platform;

namespace SweeplyForWindows.Monitor;

/// <summary>
/// Samples system activity once a second, and temperatures every 10 seconds, while anything shows
/// them (icon number, icon details, the floating bar or the Monitor page), and stops sampling when
/// nothing does. While the Monitor page is shown it also reads the graphics cards' use, and the
/// temperatures every 2 seconds.
/// </summary>
internal sealed class MonitorController : IDisposable
{
    // Temperatures change slowly; there is no need to ask the drives every second.
    private static readonly TimeSpan ThermalEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PageThermalEvery = TimeSpan.FromSeconds(2);

    private readonly Settings _settings;
    private readonly TrayIcon _tray;
    private readonly Dispatcher _dispatcher;
    private readonly MonitorBarViewModel _barModel = new();
    private readonly SemaphoreSlim _thermalWake = new(0, 1); // the page opened: read the temperatures now
    private MonitorBar? _bar;
    private bool _barAtCorner; // placed in the corner by the app, not dragged: keep its right edge there
    private CancellationTokenSource? _sampling;
    private volatile bool _pageShown;
    private IReadOnlyList<ThermalReading> _thermals = Array.Empty<ThermalReading>(); // the latest, used every second
    private string? _iconKey; // what the icon currently shows, to redraw only on change

    public MonitorController(Settings settings, TrayIcon tray, Dispatcher dispatcher)
    {
        _settings = settings;
        _tray = tray;
        _dispatcher = dispatcher;
    }

    public event Action? OpenRequested;

    /// <summary>The floating bar's own menu asked to hide it.</summary>
    public event Action? HideBarRequested;

    /// <summary>A new second of activity, on the UI thread.</summary>
    public event Action<SystemSample>? Sampled;

    /// <summary>New temperatures, on the UI thread.</summary>
    public event Action<IReadOnlyList<ThermalReading>>? ThermalsRead;

    /// <summary>Brings everything in line with the current settings.</summary>
    public void Apply()
    {
        if (_settings.TrayDisplay == TrayDisplay.AppIcon) ShowAppIcon();
        if (!_settings.TrayToolTipStats) _tray.SetToolTip("SweeplyForWindows");

        if (_settings.ShowMonitorBar) ShowBar();
        else CloseBar();

        bool needed = _pageShown || _settings.TrayDisplay != TrayDisplay.AppIcon || _settings.TrayToolTipStats || _settings.ShowMonitorBar;
        if (needed) StartSampling();
        else StopSampling();
    }

    /// <summary>The Monitor page came into view (window shown, not minimized, page selected) or went out of it.</summary>
    public void SetPageShown(bool shown)
    {
        if (_pageShown == shown) return;
        _pageShown = shown;
        Apply();
        if (shown && _thermalWake.CurrentCount == 0) _thermalWake.Release();
    }

    public void Dispose()
    {
        StopSampling();
        CloseBar();
        // _thermalWake is not disposed: a loop that is just finishing may still touch it, and it holds no handle.
    }

    private void StartSampling()
    {
        if (_sampling is not null) return;
        _sampling = new CancellationTokenSource();
        var token = _sampling.Token;
        Task.Run(async () =>
        {
            // The sampler lives and dies on this worker, so stopping can never dispose it mid-sample.
            using var sampler = new SystemSampler();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    var sample = sampler.Sample(includeGpu: _pageShown); // only the page shows the graphics cards
                    bool hasCpu = sampler.HasCpu, hasDisk = sampler.HasDiskWrite;
                    _ = _dispatcher.BeginInvoke(() => // not awaited: the next tick must not wait for the screen
                    {
                        if (token.IsCancellationRequested) return;
                        Show(sample, hasCpu, hasDisk);
                        Sampled?.Invoke(sample);
                    });
                }
            }
            catch (OperationCanceledException) { }
        });

        // A separate loop, so a drive that is slow to answer never holds up the numbers above.
        Task.Run(async () =>
        {
            using var thermals = new ThermalSampler();
            try
            {
                while (true)
                {
                    IReadOnlyList<ThermalReading> readings;
                    try { readings = thermals.Read(); }
                    catch (Exception) { readings = Array.Empty<ThermalReading>(); } // temperatures are extra; never stop for them
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        _thermals = readings;
                        ThermalsRead?.Invoke(readings);
                    });
                    await _thermalWake.WaitAsync(_pageShown ? PageThermalEvery : ThermalEvery, token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StopSampling()
    {
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = null;
        _thermals = Array.Empty<ThermalReading>();
    }

    private void Show(SystemSample sample, bool hasCpu, bool hasDisk)
    {
        var culture = Loc.Instance.Culture;
        string download = RateFormatter.Format(sample.DownloadBytesPerSecond, culture);
        string upload = RateFormatter.Format(sample.UploadBytesPerSecond, culture);
        string cpu = hasCpu ? RateFormatter.Percent(sample.CpuPercent, culture) : "—";
        string disk = hasDisk ? RateFormatter.Format(sample.DiskWriteBytesPerSecond, culture) : "—";
        var hottest = ThermalSampler.Hottest(_thermals);

        if (_settings.TrayDisplay != TrayDisplay.AppIcon)
        {
            string text = _settings.TrayDisplay switch
            {
                TrayDisplay.Cpu => cpu,
                TrayDisplay.Download => RateFormatter.Compact(sample.DownloadBytesPerSecond, culture),
                TrayDisplay.Upload => RateFormatter.Compact(sample.UploadBytesPerSecond, culture),
                TrayDisplay.DiskWrite => hasDisk ? RateFormatter.Compact(sample.DiskWriteBytesPerSecond, culture) : "—",
                TrayDisplay.Temperature => hottest is { } h ? RateFormatter.CompactCelsius(h.Celsius, culture) : "—",
                _ => "",
            };
            int size = _tray.IconSize;
            string key = $"{text}@{size}";
            if (key != _iconKey)
            {
                _tray.SetIcon(IconFactory.ToIconHandle(TrayIconRenderer.Render(text, size)));
                _iconKey = key;
            }
        }

        if (_settings.TrayToolTipStats)
        {
            var loc = Loc.Instance;
            var tip = new StringBuilder("SweeplyForWindows");
            tip.Append('\n').Append(loc.Format("monitor.download", download)).Append("  ").Append(loc.Format("monitor.upload", upload));
            tip.Append('\n').Append(loc.Format("monitor.cpu", cpu)).Append("  ").Append(loc.Format("monitor.diskWrite", disk));
            // The hottest drive and the hottest graphics card; parts that can't be read are left out.
            var parts = new[] { ThermalPart.Disk, ThermalPart.Graphics }
                .Select(part => ThermalSampler.Hottest(_thermals, part) is { } h
                    ? loc.Format($"monitor.temp.{part}", RateFormatter.Celsius(h.Celsius, culture))
                    : null)
                .OfType<string>()
                .ToList();
            if (parts.Count > 0) tip.Append('\n').Append(loc.Format("monitor.temperature", string.Join("  ", parts)));
            _tray.SetToolTip(tip.ToString());
        }

        if (_bar is not null)
        {
            _barModel.Download = download;
            _barModel.Upload = upload;
            _barModel.Cpu = hasCpu ? cpu + "%" : "—";
            _barModel.DiskWrite = disk;
            _barModel.Temperature = hottest is { } h ? RateFormatter.Celsius(h.Celsius, culture) : "";
            _barModel.TemperatureDetails = string.Join("\n", _thermals.Select(t => $"{t.Name}  {RateFormatter.Celsius(t.Celsius, culture)}"));
        }
    }

    private void ShowAppIcon()
    {
        if (_iconKey == "app") return;
        _tray.SetIcon(IconFactory.ToIconHandle(IconFactory.AppIcon(_tray.IconSize)));
        _iconKey = "app";
    }

    private void ShowBar()
    {
        if (_bar is not null) return;
        _bar = new MonitorBar(_barModel);
        _bar.OpenRequested += () => OpenRequested?.Invoke();
        _bar.HideRequested += () => HideBarRequested?.Invoke();
        _bar.Moved += (left, top) =>
        {
            _barAtCorner = false;
            _settings.MonitorBarLeft = left;
            _settings.MonitorBarTop = top;
            _settings.Save();
        };
        // The bar gets wider when the temperature appears a moment after it opens.
        _bar.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || _bar is null) return;
            if (_barAtCorner) PlaceInCorner(_bar);
            else
            {
                // Dragged there earlier (perhaps while it was narrower): keep it on the screen.
                double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
                if (_bar.Left + _bar.ActualWidth > right) _bar.Left = Math.Max(SystemParameters.VirtualScreenLeft, right - _bar.ActualWidth);
            }
        };

        _bar.WindowStartupLocation = WindowStartupLocation.Manual;
        bool restored = false;
        if (_settings.MonitorBarLeft is double savedLeft && _settings.MonitorBarTop is double savedTop
            && IsOnScreen(savedLeft, savedTop))
        {
            _bar.Left = savedLeft;
            _bar.Top = savedTop;
            restored = true;
        }
        else
        {
            _bar.Left = -32000; // measured first, then placed; avoids a flash in the wrong place
            _bar.Top = -32000;
        }
        _barAtCorner = !restored;
        _bar.Show();
        if (!restored)
        {
            _bar.UpdateLayout();
            PlaceInCorner(_bar);
        }
    }

    private static void PlaceInCorner(MonitorBar bar)
    {
        var area = SystemParameters.WorkArea;
        bar.Left = area.Right - bar.ActualWidth - 16;
        bar.Top = area.Bottom - bar.ActualHeight - 16;
    }

    private void CloseBar()
    {
        _bar?.Close();
        _bar = null;
    }

    /// <summary>The saved spot is still on a connected monitor (screens can be unplugged).</summary>
    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 20 &&
        top >= SystemParameters.VirtualScreenTop - 20 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;
}
