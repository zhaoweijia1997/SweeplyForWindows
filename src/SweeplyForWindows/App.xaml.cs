using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sweeply.Core;
using SweeplyForWindows.Localization;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var settings = Settings.Load();
        Loc.Instance.SetLanguage(settings.Language ?? Loc.FromSystem());
        var viewModel = new MainViewModel(KnownPaths.FromSystem(), settings);
        new MainWindow(viewModel).Show();
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
        shots.Add((1, "support", "en", ThemeMode.Light));
        shots.Add((1, "support", "zh-Hans", ThemeMode.Light));
        shots.Add((2, "about", "en", ThemeMode.Light));

        foreach (var (page, name, lang, theme) in shots)
        {
            Loc.Instance.SetLanguage(lang);
            var vm = new MainViewModel(KnownPaths.FromSystem(), new Settings());
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
