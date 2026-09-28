using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SweeplyForWindows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SweeplyForWindows.exe --snapshot <folder>
        // Renders the main window to PNG (light and dark) without touching the desktop,
        // so screenshots can never contain other windows or personal files.
        if (e.Args.Length == 2 && e.Args[0] == "--snapshot")
        {
            // Closing one snapshot window must not end the app before the next one is drawn.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = 0;
            try { Snapshot.RenderAll(e.Args[1]); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); code = 1; }
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }
}

internal static class Snapshot
{
    public static void RenderAll(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (theme, name) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
        {
            var window = new MainWindow
            {
                ThemeMode = theme,
                // Shown far outside every monitor so the theme and layout are really applied;
                // only this window's own visual tree is rendered below, never the screen.
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Save(window, theme == ThemeMode.Dark, Path.Combine(folder, $"main-{name}.png"));
            window.Close();
        }
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
