using System.Windows.Media;
using System.Windows.Media.Imaging;
using static SweeplyForWindows.Platform.NativeMethods;

namespace SweeplyForWindows.Platform;

internal static class IconFactory
{
    /// <summary>The app icon at exactly <paramref name="size"/> pixels, from the frame closest in size.</summary>
    public static BitmapSource AppIcon(int size)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames
            .OrderBy(f => f.PixelWidth >= size ? f.PixelWidth - size : 10_000 - f.PixelWidth)
            .First();
        if (frame.PixelWidth == size) return frame;
        double scale = size / (double)frame.PixelWidth;
        return new TransformedBitmap(frame, new ScaleTransform(scale, scale));
    }

    /// <summary>Creates a Windows icon handle from a bitmap. The caller owns it (DestroyIcon).</summary>
    public static IntPtr ToIconHandle(BitmapSource source)
    {
        // Icons store plain (not premultiplied) alpha.
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        // The 1-bit mask is all zeros: transparency comes from the alpha channel.
        int maskStride = (width + 15) / 16 * 2;
        IntPtr color = CreateBitmap(width, height, 1, 32, pixels);
        IntPtr mask = CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }
}
