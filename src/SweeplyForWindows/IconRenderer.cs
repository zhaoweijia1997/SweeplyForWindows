using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SweeplyForWindows;

/// <summary>
/// Draws the app icon in code (a broom on a blue-to-teal tile) and writes
/// icon.png (256 px) plus a multi-size app.ico. Run with: SweeplyForWindows.exe --render-icon &lt;folder&gt;
/// </summary>
internal static class IconRenderer
{
    private static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void RenderAll(string folder)
    {
        Directory.CreateDirectory(folder);
        var pngs = new List<(int Size, byte[] Png)>();
        foreach (int size in IcoSizes) pngs.Add((size, RenderPng(size)));

        File.WriteAllBytes(Path.Combine(folder, "icon.png"), pngs.First(p => p.Size == 256).Png);
        WriteIco(Path.Combine(folder, "app.ico"), pngs);
    }

    private static byte[] RenderPng(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = size / 256.0;
            dc.PushTransform(new ScaleTransform(s, s));
            Draw(dc);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Everything in a 256 x 256 design space.</summary>
    private static void Draw(DrawingContext dc)
    {
        // Tile
        var tile = new LinearGradientBrush(Color.FromRgb(0x1E, 0x6F, 0xE0), Color.FromRgb(0x1F, 0xC8, 0xC8), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(tile, null, new Rect(12, 12, 232, 232), 56, 56);
        // Soft highlight on the upper half
        var gloss = new LinearGradientBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), new Point(0.5, 0), new Point(0.5, 0.6));
        dc.DrawRoundedRectangle(gloss, null, new Rect(12, 12, 232, 232), 56, 56);

        var white = Brushes.White;
        var shade = new SolidColorBrush(Color.FromArgb(0x40, 0x0B, 0x3D, 0x7A));

        // Broom, tilted
        dc.PushTransform(new RotateTransform(-38, 128, 132));
        // Handle
        dc.DrawRoundedRectangle(white, null, new Rect(119, 34, 18, 104), 9, 9);
        // Binding band
        dc.DrawRoundedRectangle(white, null, new Rect(96, 134, 64, 20), 6, 6);
        dc.DrawRectangle(shade, null, new Rect(96, 148, 64, 6));
        // Bristles: a trapezoid flaring out
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(100, 156), true, true);
            g.LineTo(new Point(156, 156), true, false);
            g.LineTo(new Point(180, 216), true, false);
            g.QuadraticBezierTo(new Point(128, 228), new Point(76, 216), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(white, null, head);
        // Bristle grooves
        var groove = new Pen(shade, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var (x1, x2) in new[] { (114.0, 104.0), (128.0, 128.0), (142.0, 152.0) })
            dc.DrawLine(groove, new Point(x1, 166), new Point(x2, 212));
        dc.Pop();

        // Sparkles
        DrawSparkle(dc, white, new Point(188, 70), 22);
        DrawSparkle(dc, white, new Point(212, 112), 12);
    }

    private static void DrawSparkle(DrawingContext dc, Brush brush, Point c, double r)
    {
        double k = r * 0.28;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
            ctx.QuadraticBezierTo(new Point(c.X + k, c.Y - k), new Point(c.X + r, c.Y), true, false);
            ctx.QuadraticBezierTo(new Point(c.X + k, c.Y + k), new Point(c.X, c.Y + r), true, false);
            ctx.QuadraticBezierTo(new Point(c.X - k, c.Y + k), new Point(c.X - r, c.Y), true, false);
            ctx.QuadraticBezierTo(new Point(c.X - k, c.Y - k), new Point(c.X, c.Y - r), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    /// <summary>ICO container with PNG-encoded entries (supported since Windows Vista).</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> images)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width (0 = 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);          // palette
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
