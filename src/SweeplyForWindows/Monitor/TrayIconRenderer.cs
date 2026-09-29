using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SweeplyForWindows.Monitor;

/// <summary>Draws a short live number ("37", "1.2M") as a notification-area icon.</summary>
internal static class TrayIconRenderer
{
    // The app icon's blue, darkened toward the teal end so white text stays readable at 16 px.
    private static readonly Brush Tile = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x1A, 0x5F, 0xC8), Color.FromRgb(0x13, 0x86, 0x94), new Point(0, 0), new Point(1, 1)));

    // Bahnschrift SemiCondensed ships with Windows 10 and 11 and fits four characters into a 16 px icon.
    private static readonly Typeface Face = new(
        new FontFamily("Bahnschrift SemiCondensed, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // One canvas, cleared and reused for every redraw. A new RenderTargetBitmap each second holds
    // GDI resources until the garbage collector happens to run (measured: 23 -> 104 GDI objects in
    // 90 s), and a process that runs for days must not depend on that.
    private static RenderTargetBitmap? _canvas;

    /// <summary>
    /// Draws <paramref name="text"/> on the shared canvas and returns it. The result is overwritten by
    /// the next call, so use it straight away (e.g. <see cref="Platform.IconFactory.ToIconHandle"/>).
    /// </summary>
    public static BitmapSource Render(string text, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double radius = Math.Max(2, size * 0.18);
            dc.DrawRoundedRectangle(Tile, null, new Rect(0, 0, size, size), radius, radius);

            // Largest font that still fits, leaving one pixel either side.
            double fontSize = size * 0.8;
            FormattedText formatted;
            do
            {
                formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Face, fontSize, Brushes.White, 1.0);
                if (formatted.Width <= size - 2) break;
                fontSize -= 0.5;
            } while (fontSize > 5);

            // Center on the digits' visual box, not the line box, so the number sits in the middle.
            var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
            double x = (size - ink.Width) / 2 - ink.X;
            double y = (size - ink.Height) / 2 - ink.Y;
            dc.DrawText(formatted, new Point(Math.Round(x), Math.Round(y)));
        }
        if (_canvas is null || _canvas.PixelWidth != size)
            _canvas = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        else
            _canvas.Clear();
        _canvas.Render(visual);
        return _canvas;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
