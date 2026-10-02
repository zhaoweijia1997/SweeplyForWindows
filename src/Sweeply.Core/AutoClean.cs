namespace Sweeply.Core;

/// <summary>
/// "Clean up every day": while the app runs in the notification area, once a day it moves what a press
/// of "Move to Recycle Bin" would move with the usual choices, minus the categories that cost more to
/// clean every day than they free (<see cref="NotDaily"/>). Same scan, same check right before moving,
/// same Recycle Bin and undo as a clean by hand; only the list is not shown first. Off unless turned on.
/// </summary>
public static class AutoClean
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>
    /// Ticked by default but never cleaned automatically. Package caches: cleaned every day, everything is
    /// downloaded again, and a build without network fails. Shader caches: games and apps compile them
    /// again, so their next start stutters. A clean by hand still offers them.
    /// </summary>
    public static IReadOnlySet<string> NotDaily { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "npm-cache", "pip-cache", "nuget-cache", "yarn-cache", "gradle-cache", "go-cache", "cargo-cache",
        "gpu-shader-cache",
    };

    public static bool IsIncluded(CleanupCategory category) =>
        category.SelectedByDefault && category.Group != CategoryGroup.Folder && !NotDaily.Contains(category.Id);

    /// <param name="lastUtc">The last automatic clean; null when there has been none since it was turned on.</param>
    public static bool IsDue(bool enabled, DateTime? lastUtc, DateTime nowUtc)
    {
        if (!enabled) return false;
        if (lastUtc is null) return true;
        if (lastUtc > nowUtc) return true; // the clock was set back: clean rather than wait for years
        return nowUtc - lastUtc.Value >= Every;
    }

    /// <summary>
    /// Scans the included categories and moves what they found. Items the Recycle Bin could not take are
    /// left alone: done by hand, Windows asks before deleting such an item for good, and here nobody is
    /// there to ask.
    /// </summary>
    /// <param name="fitsInRecycleBin">Path and size → would really go to the Recycle Bin.</param>
    public static CleanOutcome Run(
        IEnumerable<CleanupCategory> categories,
        IRecycleBin bin,
        DateTime nowUtc,
        IReadOnlyCollection<string>? excluded = null,
        Func<string, bool>? isRunning = null,
        Func<string, bool>? isLocalFixedDrive = null,
        Func<string, long, bool>? fitsInRecycleBin = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancel = default)
    {
        fitsInRecycleBin ??= RecycleBinCapacity.Fits;
        var selection = new List<(CleanupItem, CleanupCategory)>();
        var tooBig = new List<SkippedItem>();
        foreach (var category in categories.Where(IsIncluded))
        {
            cancel.ThrowIfCancellationRequested();
            var scan = Scanner.Scan(category, nowUtc, isRunning, excluded);
            if (scan.Status != ScanStatus.Found) continue;
            foreach (var item in scan.Items)
            {
                if (fitsInRecycleBin(item.Path, item.Bytes)) selection.Add((item, category));
                else tooBig.Add(new SkippedItem(item, SafetyVerdict.Ok, "does not fit in the Recycle Bin"));
            }
        }

        var outcome = Cleaner.Clean(selection, bin, nowUtc, progress, cancel, isRunning, isLocalFixedDrive, excluded);
        return tooBig.Count == 0 ? outcome : outcome with { Skipped = outcome.Skipped.Concat(tooBig).ToList() };
    }
}
