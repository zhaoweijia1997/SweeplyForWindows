namespace Sweeply.Core;

public enum ReminderInterval
{
    Off,
    Weekly,
    Monthly,
}

/// <summary>
/// "Remind me to clean up": every week or month the app looks (only reads) and says so in a
/// notification when enough can be cleaned. A clean done by hand starts the wait again.
/// </summary>
public static class Reminder
{
    /// <summary>Less than this is not worth interrupting anyone for.</summary>
    public const long ThresholdBytes = 1L << 30; // 1 GB

    public static TimeSpan Length(ReminderInterval interval) => interval switch
    {
        ReminderInterval.Weekly => TimeSpan.FromDays(7),
        ReminderInterval.Monthly => TimeSpan.FromDays(30),
        _ => TimeSpan.MaxValue,
    };

    /// <param name="lastUtc">The last look or clean; null when there has been none yet.</param>
    public static bool IsDue(ReminderInterval interval, DateTime? lastUtc, DateTime nowUtc)
    {
        if (interval == ReminderInterval.Off || lastUtc is null) return false;
        if (lastUtc > nowUtc) return true; // the clock was set back: look rather than wait for years
        return nowUtc - lastUtc.Value >= Length(interval);
    }

    /// <summary>
    /// What one press of "Move to Recycle Bin" would move with the usual choices: the categories
    /// ticked by default, skipping those whose app is open and anything on the "Never clean" list.
    /// </summary>
    public static long CleanableBytes(IEnumerable<CleanupCategory> categories, DateTime nowUtc,
        IReadOnlyCollection<string>? excluded = null, Func<string, bool>? isRunning = null) =>
        categories.Where(c => c.SelectedByDefault)
            .Select(c => Scanner.Scan(c, nowUtc, isRunning, excluded))
            .Where(s => s.Status == ScanStatus.Found)
            .Sum(s => s.TotalBytes);
}
