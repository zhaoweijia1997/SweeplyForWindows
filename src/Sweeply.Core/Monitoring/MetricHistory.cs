namespace Sweeply.Core.Monitoring;

/// <summary>One point of a chart: how many seconds ago it was measured, and the value.</summary>
public readonly record struct ChartPoint(double SecondsAgo, double Value);

/// <summary>
/// The recent values of one measurement, for a chart that scrolls with time. Points are placed by
/// when they were measured, so a pause in sampling shows as a gap instead of squeezing the curve.
/// Kept in memory only.
/// </summary>
public sealed class MetricHistory
{
    private readonly List<(DateTime Utc, double Value)> _points = new();

    public MetricHistory(TimeSpan window) => Window = window;

    /// <summary>How far back the chart reaches.</summary>
    public TimeSpan Window { get; }

    public int Count => _points.Count;

    public void Add(DateTime utc, double value)
    {
        if (!double.IsFinite(value)) return;
        if (_points.Count > 0 && utc < _points[^1].Utc) _points.Clear(); // the clock went back
        _points.Add((utc, value));
        // Keep one point older than the window, so the line runs right up to the chart's left edge.
        int old = 0;
        while (old + 1 < _points.Count && utc - _points[old + 1].Utc >= Window) old++;
        if (old > 0) _points.RemoveRange(0, old);
    }

    public void Clear() => _points.Clear();

    /// <summary>The points as seen from <paramref name="utcNow"/>, oldest first.</summary>
    public ChartPoint[] Points(DateTime utcNow)
    {
        var result = new ChartPoint[_points.Count];
        for (int i = 0; i < _points.Count; i++)
            result[i] = new ChartPoint((utcNow - _points[i].Utc).TotalSeconds, _points[i].Value);
        return result;
    }

    /// <summary>The largest value within the window, or 0.</summary>
    public double Max(DateTime utcNow)
    {
        double max = 0;
        foreach (var (time, value) in _points)
            if (utcNow - time <= Window && value > max) max = value;
        return max;
    }

    /// <summary>The smallest value within the window, or null when there is none.</summary>
    public double? Min(DateTime utcNow)
    {
        double? min = null;
        foreach (var (time, value) in _points)
            if (utcNow - time <= Window && (min is null || value < min)) min = value;
        return min;
    }
}

/// <summary>Round chart scales, so the top of a chart reads as a plain number.</summary>
public static class ChartScale
{
    /// <summary>The smallest 1, 2 or 5 times a power of ten that is at least <paramref name="value"/>.</summary>
    public static double Nice(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double fraction = value / power;
        double nice = fraction <= 1 + 1e-9 ? 1 : fraction <= 2 + 1e-9 ? 2 : fraction <= 5 + 1e-9 ? 5 : 10;
        return nice * power;
    }

    /// <summary>
    /// A transfer-rate scale that is round in the unit it is shown in (KB/s, MB/s, GB/s, 1024-based
    /// like <see cref="RateFormatter"/>): 500 KB/s, 2 MB/s, 10 MB/s. Never below <paramref name="minimum"/>.
    /// </summary>
    public static double Rate(double bytesPerSecond, double minimum)
    {
        double value = Math.Max(double.IsFinite(bytesPerSecond) ? bytesPerSecond : 0, minimum);
        double unit = 1;
        while (value / unit >= 999.5 && unit < 1024d * 1024 * 1024) unit *= 1024;
        double nice = Nice(value / unit);
        if (nice >= 1000 && unit < 1024d * 1024 * 1024) // 1000 KB/s reads better as 1 MB/s
        {
            unit *= 1024;
            nice = Nice(value / unit);
        }
        return nice * unit;
    }
}
