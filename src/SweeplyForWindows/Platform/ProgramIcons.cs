using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SweeplyForWindows.Platform;

/// <summary>
/// Small program icons as File Explorer shows them, kept per path (the same program is asked for many times).
/// Programs whose path can't be read get Windows' generic program icon. Use on the UI thread.
/// </summary>
internal static class ProgramIcons
{
    private const uint ShgfiIcon = 0x100, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _generic;
    private static bool _genericLoaded;

    public static ImageSource? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Generic();
        if (Cache.TryGetValue(path, out var icon)) return icon;
        icon = Load(path, 0, ShgfiIcon | ShgfiSmallIcon) ?? Generic();
        if (Cache.Count > 500) Cache.Clear(); // programs come and go; the list stays small
        Cache[path] = icon;
        return icon;
    }

    /// <summary>The icon Windows shows for programs in general (from the ".exe" extension, without touching a file).</summary>
    public static ImageSource? Generic()
    {
        if (_genericLoaded) return _generic;
        _genericLoaded = true;
        return _generic = Load(".exe", FileAttributeNormal, ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
    }

    private static ImageSource? Load(string path, uint attributes, uint flags)
    {
        var info = new ShFileInfo();
        if (SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;
        try
        {
            // The pixels are copied, so the icon handle can go straight away.
            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is COMException or ArgumentException) { return null; }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
