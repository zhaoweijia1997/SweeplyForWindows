using System.Globalization;

namespace Sweeply.Core.Monitoring;

/// <summary>Formats transfer rates with the same 1024-based units as <see cref="SizeFormatter"/>.</summary>
public static class RateFormatter
{
    private static readonly string[] Units = { "KB/s", "MB/s", "GB/s" };

    /// <summary>"0 B/s", "850 KB/s", "1.2 MB/s", "12 MB/s": never more than three digits.</summary>
    public static string Format(double bytesPerSecond, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double value = Clean(bytesPerSecond);
        if (value < 999.5) return value.ToString("0", culture) + " B/s";

        int unit = -1;
        while (value >= 999.5 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string format = value < 9.95 ? "0.0" : "0";
        return value.ToString(format, culture) + " " + Units[unit];
    }

    /// <summary>
    /// At most four characters, for a notification-area icon: "0K", "85K", "0.4M", "12M", "1.2G".
    /// Anything under 1 KB/s shows as "0K" so the icon does not flicker between units when idle.
    /// </summary>
    public static string Compact(double bytesPerSecond, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double kb = Clean(bytesPerSecond) / 1024;
        if (kb < 99.5) return kb.ToString("0", culture) + "K";
        double mb = kb / 1024;
        if (mb < 9.95) return mb.ToString("0.0", culture) + "M";
        if (mb < 999.5) return mb.ToString("0", culture) + "M";
        double gb = mb / 1024;
        return gb.ToString(gb < 9.95 ? "0.0" : "0", culture) + "G";
    }

    /// <summary>A CPU percentage as a whole number, "0" to "100".</summary>
    public static string Percent(double percent, CultureInfo? culture = null) =>
        Math.Clamp(Math.Round(Clean(percent)), 0, 100).ToString("0", culture ?? CultureInfo.CurrentCulture);

    /// <summary>A temperature in whole degrees: "38°C".</summary>
    public static string Celsius(double celsius, CultureInfo? culture = null) => Degrees(celsius, culture) + "°C";

    /// <summary>A temperature for a notification-area icon: "38°".</summary>
    public static string CompactCelsius(double celsius, CultureInfo? culture = null) => Degrees(celsius, culture) + "°";

    private static string Degrees(double celsius, CultureInfo? culture)
    {
        double rounded = double.IsFinite(celsius) ? Math.Round(celsius) : 0;
        if (rounded == 0) rounded = 0; // no "-0"
        return rounded.ToString("0", culture ?? CultureInfo.CurrentCulture);
    }

    private static double Clean(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
