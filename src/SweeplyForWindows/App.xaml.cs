using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sweeply.Core;
using SweeplyForWindows.Localization;
using SweeplyForWindows.Monitor;
using SweeplyForWindows.Platform;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows;

public partial class App : Application
{
    private const int MenuOpen = 1;
    private const int MenuShowBar = 2;
    private const int MenuExit = 3;

    private SingleInstance? _instance;
    private Settings _settings = new();
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private MonitorController? _monitor;
    private ReminderController? _reminder;
    private AutoCleanController? _autoClean;
    private bool _reminderPending; // the last notification was a reminder
    private bool _autoCleanPending; // the last notification said what an automatic clean moved
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SweeplyForWindows.exe --helper <pipe> <app process id>
        // The capture helper, started by the app itself. It must not reach the single-instance check below,
        // which would hand its command line to the app and end it.
        if (e.Args.Length > 0 && e.Args[0] == Capture.HelperProcess.Argument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Capture.HelperProcess.Run(e.Args));
            return;
        }

        // SweeplyForWindows.exe --snapshot <folder>
        // Renders the pages with made-up results to PNG, without touching the desktop,
        // so screenshots can never contain other windows, real paths or personal files.
        if (e.Args.Length == 2 && e.Args[0] == "--snapshot")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing one window must not end the run
            int code = 0;
            try { Snapshot.RenderAll(e.Args[1]); }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            Shutdown(code);
            return;
        }

        // SweeplyForWindows.exe --render-icon <folder>   (writes icon.png and app.ico)
        if (e.Args.Length == 2 && e.Args[0] == "--render-icon")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            IconRenderer.RenderAll(e.Args[1]);
            Shutdown(0);
            return;
        }

        // SweeplyForWindows.exe --scan-report <file>
        // Read-only scan of this PC; writes one line per category (status, item count, size) and no paths,
        // so the report can be attached to a bug report without revealing anything personal.
        if (e.Args.Length == 2 && e.Args[0] == "--scan-report")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var lines = new List<string>();
            foreach (var category in CategoryCatalog.Create(KnownPaths.FromSystem()))
            {
                var started = DateTime.UtcNow;
                var scan = Scanner.Scan(category, DateTime.UtcNow);
                double seconds = (DateTime.UtcNow - started).TotalSeconds;
                lines.Add($"{category.Id,-22} {scan.Status,-20} items={scan.Items.Count,-5} size={SizeFormatter.Format(scan.TotalBytes, System.Globalization.CultureInfo.InvariantCulture),-10} scan={seconds:0.00}s");
            }
            File.WriteAllLines(e.Args[1], lines);
            Shutdown(0);
            return;
        }

        // A second launch (Start menu, start-up, later the folder menu) only wakes the running copy.
        // "--exit" instead asks the running copy to close, e.g. before its file is replaced by a newer one.
        var instance = new SingleInstance("SweeplyForWindows");
        bool exitRequest = e.Args.Contains("--exit");
        if (!instance.IsFirst || exitRequest)
        {
            if (!instance.IsFirst) instance.SendToFirst(e.Args);
            instance.Dispose();
            Shutdown(0);
            return;
        }
        _instance = instance;

        // The app lives in the notification area; closing the window does not end it (unless turned off).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _settings = Settings.Load();
        Loc.Instance.SetLanguage(_settings.Language ?? Loc.FromSystem());
        Autostart.Refresh();
        FolderMenu.Refresh(Loc.Instance["menu.folder"]);
        string? folder = FolderJunk.FolderFromArgs(e.Args);
        string? spaceFolder = SpaceFolderFromArgs(e.Args);
        string? captureFile = ValueAfter(e.Args, "--open-capture");

        _viewModel = new MainViewModel(KnownPaths.FromSystem(), _settings);
        // "--capture-replay <file>": Start plays that file back instead of capturing (for tests; needs no administrator rights).
        _viewModel.Capture.ReplayFile = ValueAfter(e.Args, "--capture-replay");
        _window = new MainWindow(_viewModel, scanOnOpen: folder is null && spaceFolder is null && captureFile is null);
        _window.Closing += OnWindowClosing;
        _window.Closed += (_, _) => ExitApp();

        _tray = new TrayIcon();
        _tray.SetToolTip("SweeplyForWindows");
        _tray.Clicked += ShowMainWindow;
        _tray.NotificationClicked += () =>
        {
            ShowMainWindow();
            if (_autoCleanPending)
            {
                _autoCleanPending = false;
                _viewModel.PageIndex = MainViewModel.SettingsPage; // the history, where it can be undone
                return;
            }
            if (!_reminderPending) return;
            _reminderPending = false;
            _viewModel.PageIndex = 0;
            _ = _viewModel.RefreshAfterReminderAsync();
        };
        _tray.MenuRequested += ShowTrayMenu;

        // Icon number, icon details and the floating bar; the Settings page, the icon's menu and
        // the bar's own menu all change the same settings through the view model.
        _monitor = new MonitorController(_settings, _tray, Dispatcher);
        _monitor.OpenRequested += ShowMainWindow;
        _monitor.HideBarRequested += () => _viewModel.ShowMonitorBar = false;
        _viewModel.MonitorSettingsChanged += _monitor.Apply;
        _monitor.Apply();

        // The Monitor page gets the same samples. While it is in view, sampling runs even with the
        // icon number, details and bar all off; once it is out of view, it is back to the settings.
        _monitor.Sampled += _viewModel.Monitor.OnSample;
        _monitor.ThermalsRead += _viewModel.Monitor.OnThermals;
        _window.IsVisibleChanged += (_, _) => UpdateMonitorPage();
        _window.StateChanged += (_, _) => UpdateMonitorPage();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PageIndex)) UpdateMonitorPage();
        };

        _reminder = new ReminderController(_settings, _tray, KnownPaths.FromSystem());
        _reminder.Shown += () =>
        {
            _reminderPending = true;
            _autoCleanPending = false; // the newest notification decides what a click shows
        };
        _viewModel.ReminderSettingsChanged += _reminder.Apply;
        _reminder.Apply();

        _autoClean = new AutoCleanController(_settings, _tray, _viewModel);
        _autoClean.Shown += () =>
        {
            _autoCleanPending = true;
            _reminderPending = false;
        };
        _viewModel.AutoCleanSettingsChanged += _autoClean.Apply;
        _autoClean.Apply();
        _tray.Show();

        instance.Listen(args => Dispatcher.BeginInvoke(() =>
        {
            if (args.Contains("--exit"))
            {
                ExitApp();
                return;
            }
            ShowMainWindow();
            if (FolderJunk.FolderFromArgs(args) is string chosen) _ = _viewModel!.OpenFolderAsync(chosen);
            else if (SpaceFolderFromArgs(args) is string space) _viewModel!.OpenSpace(space);
            else if (ValueAfter(args, "--open-capture") is string file) _viewModel!.OpenCapture(file);
        }));

        if (folder is not null)
        {
            ShowMainWindow();
            _ = _viewModel.OpenFolderAsync(folder);
        }
        else if (spaceFolder is not null)
        {
            ShowMainWindow();
            _viewModel.OpenSpace(spaceFolder);
        }
        else if (captureFile is not null)
        {
            ShowMainWindow();
            _viewModel.OpenCapture(captureFile);
        }
        else if (!e.Args.Contains(Autostart.BackgroundArg))
        {
            ShowMainWindow();
        }
    }

    /// <summary>The argument after <paramref name="name"/> ("--open-capture file"), without stray quotes.</summary>
    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        int i = args.ToList().IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        string value = args[i + 1].Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    /// <summary>"--space &lt;folder&gt;": open the Space page on that folder. Same quoting rules as the folder menu.</summary>
    private static string? SpaceFolderFromArgs(IReadOnlyList<string> args)
    {
        int i = args.ToList().IndexOf("--space");
        if (i < 0 || i + 1 >= args.Count) return null;
        string f = args[i + 1].Trim().TrimEnd('"');
        if (f.Length == 2 && f[1] == ':') f += '\\';
        return f.Length == 0 ? null : f;
    }

    private void UpdateMonitorPage()
    {
        if (_window is null || _viewModel is null || _monitor is null) return;
        bool shown = _window.IsVisible && _window.WindowState != WindowState.Minimized
                     && _viewModel.PageIndex == MainViewModel.MonitorPage;
        _viewModel.Monitor.SetShown(shown);
        _monitor.SetPageShown(shown);
    }

    private void ShowMainWindow()
    {
        if (_window is null || _exiting) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowTrayMenu(int x, int y)
    {
        var loc = Loc.Instance;
        int chosen = _tray!.ShowMenu(x, y, new[]
        {
            new TrayMenuItem(MenuOpen, loc["tray.open"]),
            new TrayMenuItem(MenuShowBar, loc["tray.showBar"], _settings.ShowMonitorBar),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuExit, loc["tray.exit"]),
        });
        if (chosen == MenuOpen) ShowMainWindow();
        else if (chosen == MenuShowBar) _viewModel!.ShowMonitorBar = !_settings.ShowMonitorBar;
        else if (chosen == MenuExit) ExitApp();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || !_settings.CloseToTray) return; // a real close: Closed ends the app
        e.Cancel = true;
        _window!.Hide();
        if (!_settings.TrayHintShown)
        {
            _reminderPending = false; // this notification replaces any reminder
            _autoCleanPending = false;
            _tray?.ShowNotification(Loc.Instance["tray.hint.title"], Loc.Instance["tray.hint.body"]);
            _settings.TrayHintShown = true;
            _settings.Save();
        }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _viewModel?.Capture.StopNow(); // closes the pipe; the capture helper ends with it
        _monitor?.Dispose();
        _reminder?.Dispose();
        _autoClean?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        Shutdown(0);
    }
}

internal static class Snapshot
{
    public static void RenderAll(string folder)
    {
        Directory.CreateDirectory(folder);
        var shots = new List<(int Page, string Name, string Lang, ThemeMode Theme)>();
        foreach (var lang in Loc.Languages) shots.Add((0, "clean", lang.Code, ThemeMode.Light));
        shots.Add((0, "clean", "en", ThemeMode.Dark));
        shots.Add((0, "clean", "zh-Hans", ThemeMode.Dark));
        shots.Add((MainViewModel.SettingsPage, "settings", "en", ThemeMode.Light));
        shots.Add((MainViewModel.SettingsPage, "settings", "zh-Hans", ThemeMode.Light));
        shots.Add((MainViewModel.SupportPage, "support", "en", ThemeMode.Light));
        shots.Add((MainViewModel.SupportPage, "support", "zh-Hans", ThemeMode.Light));
        shots.Add((MainViewModel.AboutPage, "about", "en", ThemeMode.Light));

        foreach (var (page, name, lang, theme) in shots)
        {
            Loc.Instance.SetLanguage(lang);
            // In-memory history: screenshots never show this PC's real cleans.
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings(), new CleanHistory(null));
            vm.LoadSample();
            vm.PageIndex = page;
            var window = new MainWindow(vm, scanOnOpen: false)
            {
                ThemeMode = theme,
                // Shown far outside every monitor so theme and layout are really applied;
                // only this window's own visual tree is rendered below, never the screen.
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            WaitForAnimations(TimeSpan.FromMilliseconds(900));
            string themeName = theme == ThemeMode.Dark ? "dark" : "light";
            Save(window, theme == ThemeMode.Dark, Path.Combine(folder, $"{name}-{lang}-{themeName}.png"));
            window.Close();
        }

        // "Clean up with SweeplyForWindows" on a folder.
        foreach (var lang in new[] { "en", "zh-Hans" })
        {
            Loc.Instance.SetLanguage(lang);
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings(), new CleanHistory(null));
            vm.LoadFolderSample();
            var window = new MainWindow(vm, scanOnOpen: false)
            {
                ThemeMode = ThemeMode.Light,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            WaitForAnimations(TimeSpan.FromMilliseconds(900));
            Save(window, false, Path.Combine(folder, $"folder-{lang}-light.png"));
            window.Close();
        }

        // The Space page: the folder list and the largest files, with made-up results.
        foreach (var (lang, tab, name) in new[]
                 {
                     ("en", 0, "space"), ("zh-Hans", 0, "space"), ("en", 1, "space-largest"), ("zh-Hans", 1, "space-largest"),
                     ("en", 2, "space-kinds"), ("zh-Hans", 2, "space-kinds"),
                 })
        {
            Loc.Instance.SetLanguage(lang);
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings(), new CleanHistory(null));
            vm.LoadSample();
            vm.Space.LoadSample();
            vm.Space.TabIndex = tab;
            vm.PageIndex = MainViewModel.SpacePage;
            var window = new MainWindow(vm, scanOnOpen: false)
            {
                ThemeMode = ThemeMode.Light,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            WaitForAnimations(TimeSpan.FromMilliseconds(900));
            Save(window, false, Path.Combine(folder, $"{name}-{lang}-light.png"));
            window.Close();
        }

        // The Monitor page, with made-up activity and hardware.
        foreach (var (lang, theme, tab, name) in new[]
                 {
                     ("en", ThemeMode.Light, MonitorViewModel.PerformanceTab, "monitor"),
                     ("zh-Hans", ThemeMode.Light, MonitorViewModel.PerformanceTab, "monitor"),
                     ("zh-Hans", ThemeMode.Dark, MonitorViewModel.PerformanceTab, "monitor"),
                     ("en", ThemeMode.Light, MonitorViewModel.HardwareTab, "monitor-hardware"),
                     ("zh-Hans", ThemeMode.Light, MonitorViewModel.HardwareTab, "monitor-hardware"),
                     ("en", ThemeMode.Light, MonitorViewModel.NetworkTab, "monitor-network"),
                     ("zh-Hans", ThemeMode.Light, MonitorViewModel.NetworkTab, "monitor-network"),
                     ("en", ThemeMode.Light, MonitorViewModel.ToolsTab, "monitor-tools"),
                     ("zh-Hans", ThemeMode.Light, MonitorViewModel.ToolsTab, "monitor-tools"),
                     ("en", ThemeMode.Light, MonitorViewModel.ConnectionsTab, "monitor-connections"),
                     ("zh-Hans", ThemeMode.Light, MonitorViewModel.ConnectionsTab, "monitor-connections"),
                 })
        {
            Loc.Instance.SetLanguage(lang);
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings(), new CleanHistory(null));
            vm.LoadSample();
            vm.Monitor.LoadSample();
            vm.Monitor.TabIndex = tab;
            vm.PageIndex = MainViewModel.MonitorPage;
            var window = new MainWindow(vm, scanOnOpen: false)
            {
                ThemeMode = theme,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            WaitForAnimations(TimeSpan.FromMilliseconds(900));
            string themeName = theme == ThemeMode.Dark ? "dark" : "light";
            Save(window, theme == ThemeMode.Dark, Path.Combine(folder, $"{name}-{lang}-{themeName}.png"));
            window.Close();
        }

        // The Capture page with a made-up capture.
        foreach (var (lang, theme) in new[] { ("en", ThemeMode.Light), ("zh-Hans", ThemeMode.Light), ("zh-Hans", ThemeMode.Dark) })
        {
            Loc.Instance.SetLanguage(lang);
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings(), new CleanHistory(null));
            vm.LoadSample();
            vm.Capture.LoadSample();
            vm.PageIndex = MainViewModel.CapturePage;
            var window = new MainWindow(vm, scanOnOpen: false)
            {
                ThemeMode = theme,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 1280,
                Height = 820,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            WaitForAnimations(TimeSpan.FromMilliseconds(900));
            Save(window, theme == ThemeMode.Dark, Path.Combine(folder, $"capture-{lang}-{(theme == ThemeMode.Dark ? "dark" : "light")}.png"));
            window.Close();
        }

        // The list shown before anything is moved.
        foreach (var lang in new[] { "en", "zh-Hans" })
        {
            Loc.Instance.SetLanguage(lang);
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings(), new CleanHistory(null));
            vm.LoadSample();
            vm.OpenReview();
            var window = new MainWindow(vm, scanOnOpen: false)
            {
                ThemeMode = ThemeMode.Light,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            WaitForAnimations(TimeSpan.FromMilliseconds(900));
            Save(window, false, Path.Combine(folder, $"review-{lang}-light.png"));
            window.Close();
        }

        RenderTrayIcons(Path.Combine(folder, "tray-icons.png"));
        foreach (var lang in new[] { "en", "zh-Hans" })
        {
            Loc.Instance.SetLanguage(lang);
            RenderMonitorBar(Path.Combine(folder, $"monitor-bar-{lang}.png"));
        }
    }

    /// <summary>Every kind of icon number at the three common icon sizes, enlarged 4x to check pixels.</summary>
    private static void RenderTrayIcons(string path)
    {
        string[] texts = { "3", "37", "100", "0K", "85K", "0.4M", "12M", "123M", "1.2G", "—", "39°", "105°" };
        int[] sizes = { 16, 24, 32 };
        const int zoom = 4, gap = 4;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, texts.Length * (32 + gap) * zoom, sizes.Sum(s => s + gap) * zoom));
            double y = 0;
            foreach (int size in sizes)
            {
                for (int i = 0; i < texts.Length; i++)
                    // Copied, because the renderer reuses one canvas for every call.
                    dc.DrawImage(new WriteableBitmap(Monitor.TrayIconRenderer.Render(texts[i], size)), new Rect(i * (32 + gap) * zoom, y, size * zoom, size * zoom));
                y += (size + gap) * zoom;
            }
        }
        var bitmap = new RenderTargetBitmap(texts.Length * (32 + gap) * zoom, sizes.Sum(s => s + gap) * zoom, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RenderMonitorBar(string path)
    {
        var model = new Monitor.MonitorBarViewModel
        {
            Download = "1.2 MB/s", Upload = "35 KB/s", Cpu = "37%", DiskWrite = "3.4 MB/s", Temperature = "41°C",
        };
        var bar = new Monitor.MonitorBar(model)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        bar.Show();
        WaitForAnimations(TimeSpan.FromMilliseconds(300));
        var root = (FrameworkElement)VisualTreeHelper.GetChild(bar, 0);
        const double dpi = 192;
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi / 96), (int)(root.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        bar.Close();
    }

    /// <summary>Keeps the message loop running for a while so expand animations finish before rendering.</summary>
    private static void WaitForAnimations(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Window window, bool dark, string path)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);

        // On Windows 11 the Fluent window background is the system Mica backdrop, which the
        // window does not draw itself, so paint the theme's base colour underneath.
        var background = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush
            ?? new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, rect);
            dc.DrawRectangle(new VisualBrush(root), null, rect);
        }

        const double dpi = 144; // 1.5x for crisp README images
        var bitmap = new RenderTargetBitmap((int)(rect.Width * dpi / 96), (int)(rect.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
