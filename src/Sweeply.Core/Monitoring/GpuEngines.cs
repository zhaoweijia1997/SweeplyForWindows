using System.Globalization;

namespace Sweeply.Core.Monitoring;

/// <summary>How busy the graphics cards are: the busiest engine, and the card it belongs to.</summary>
public readonly record struct GpuUsage(double Percent, long AdapterLuid);

/// <summary>
/// Turns the "GPU Engine" performance counters into the figure Task Manager shows. Each counter
/// instance is one process on one engine of one card, named like
/// "pid_912_luid_0x00000000_0x00011EB5_phys_0_eng_0_engtype_3D". An engine's use is the sum over
/// all processes; the figure is the busiest engine of any card.
/// </summary>
public static class GpuEngines
{
    public static GpuUsage? Busiest(IEnumerable<(string Instance, double Percent)> samples)
    {
        var engines = new Dictionary<string, (long Luid, double Sum)>(StringComparer.Ordinal);
        var instancesPerCard = new Dictionary<long, int>();
        foreach (var (instance, percent) in samples)
        {
            int at = instance.IndexOf("luid_", StringComparison.Ordinal);
            int type = instance.IndexOf("_engtype_", StringComparison.Ordinal);
            if (at < 0 || type < at || !TryParseLuid(instance.AsSpan(at + 5), out long luid)) continue;
            string engine = instance[at..type]; // "luid_0x00000000_0x00011EB5_phys_0_eng_0"
            double value = double.IsFinite(percent) && percent > 0 ? percent : 0;
            engines[engine] = (luid, (engines.TryGetValue(engine, out var known) ? known.Sum : 0) + value);
            instancesPerCard[luid] = instancesPerCard.GetValueOrDefault(luid) + 1;
        }
        if (engines.Count == 0) return null;

        var busiest = engines.Values.MaxBy(e => e.Sum);
        // Nothing busy: name the card most processes use, not whichever engine happened to come first.
        long card = busiest.Sum > 0 ? busiest.Luid : instancesPerCard.MaxBy(c => c.Value).Key;
        return new GpuUsage(Math.Clamp(busiest.Sum, 0, 100), card);
    }

    /// <summary>"0x00000000_0x00011EB5..." (high part, then low part) as one LUID number.</summary>
    internal static bool TryParseLuid(ReadOnlySpan<char> text, out long luid)
    {
        luid = 0;
        if (text.Length < 21 || !text.StartsWith("0x") || text[10] != '_' || !text[11..].StartsWith("0x")) return false;
        if (!int.TryParse(text[2..10], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int high) ||
            !uint.TryParse(text[13..21], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
            return false;
        luid = GraphicsAdapters.Luid(high, low);
        return true;
    }
}
