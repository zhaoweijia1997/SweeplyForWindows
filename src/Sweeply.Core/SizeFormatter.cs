using System.Globalization;

namespace Sweeply.Core;

/// <summary>
/// Formats byte counts the way File Explorer does: 1024-based units labelled KB / MB / GB / TB.
/// </summary>
public static class SizeFormatter
{
    private static readonly string[] Units = { "KB", "MB", "GB", "TB" };

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes < 1024) return bytes.ToString(culture) + " B";

        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        // Three significant digits, like Explorer: 1.23 GB, 12.3 GB, 123 GB.
        string format = value < 10 ? "0.00" : value < 100 ? "0.0" : "0";
        return value.ToString(format, culture) + " " + Units[unit];
    }
}
