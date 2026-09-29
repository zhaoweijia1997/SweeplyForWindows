namespace Sweeply.Core;

public sealed record SkippedItem(CleanupItem Item, SafetyVerdict Verdict, string? Error = null);

public sealed record CleanOutcome(int MovedCount, long MovedBytes, IReadOnlyList<SkippedItem> Skipped)
{
    /// <summary>Exactly what went to the Recycle Bin, so the clean can be undone.</summary>
    public IReadOnlyList<CleanupItem> Moved { get; init; } = Array.Empty<CleanupItem>();
}

/// <summary>Moves the chosen items to the Recycle Bin, checking each one again right before.</summary>
public static class Cleaner
{
    public static CleanOutcome Clean(
        IReadOnlyList<(CleanupItem Item, CleanupCategory Category)> selection,
        IRecycleBin bin,
        DateTime nowUtc,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancel = default,
        Func<string, bool>? isRunning = null,
        Func<string, bool>? isLocalFixedDrive = null)
    {
        int moved = 0;
        long bytes = 0;
        var skipped = new List<SkippedItem>();
        var movedItems = new List<CleanupItem>();

        for (int i = 0; i < selection.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (item, category) = selection[i];

            var verdict = SafetyCheck.Check(item, category, nowUtc, isRunning, isLocalFixedDrive);
            if (verdict != SafetyVerdict.Ok)
            {
                skipped.Add(new SkippedItem(item, verdict));
            }
            else if (bin.TryRecycle(item.Path, out string? error))
            {
                moved++;
                bytes += item.Bytes;
                movedItems.Add(item);
            }
            else
            {
                skipped.Add(new SkippedItem(item, SafetyVerdict.Ok, error));
            }
            progress?.Report((i + 1, selection.Count));
        }
        return new CleanOutcome(moved, bytes, skipped) { Moved = movedItems };
    }
}
