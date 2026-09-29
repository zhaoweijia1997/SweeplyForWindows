using System.Diagnostics;
using System.IO.Enumeration;

namespace Sweeply.Core;

/// <summary>A file or folder that can be moved to the Recycle Bin.</summary>
public sealed record CleanupItem(string Path, bool IsDirectory, long Bytes, DateTime LastWriteUtc);

public enum ScanStatus
{
    Found,
    NothingFound,
    /// <summary>An app that owns these files is running, so the category was not scanned.</summary>
    BlockedByRunningApp,
}

public sealed record CategoryScan(
    CleanupCategory Category,
    IReadOnlyList<CleanupItem> Items,
    ScanStatus Status,
    string? BlockingProcess = null)
{
    public long TotalBytes => Items.Sum(i => i.Bytes);

    /// <summary>Items left out because they are on the "Never clean" list.</summary>
    public int Excluded { get; init; }
}

public static class Processes
{
    public static bool IsRunning(string name)
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }
}

public static class Scanner
{
    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    public static CategoryScan Scan(CleanupCategory category, DateTime nowUtc, Func<string, bool>? isRunning = null,
        IReadOnlyCollection<string>? excluded = null)
    {
        isRunning ??= Processes.IsRunning;
        foreach (string process in category.BlockingProcesses)
        {
            if (isRunning(process))
                return new CategoryScan(category, Array.Empty<CleanupItem>(), ScanStatus.BlockedByRunningApp, process);
        }

        var items = new List<CleanupItem>();
        int excludedCount = 0;
        foreach (string root in category.Roots)
        {
            var dir = new DirectoryInfo(root);
            if (!dir.Exists || FileTree.IsReparsePoint(dir)) continue;

            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", TopLevel).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (FileTree.IsReparsePoint(entry)) continue; // never offer or follow links
                if (!Matches(category, entry)) continue;
                if (Exclusions.IsExcluded(entry.FullName, excluded))
                {
                    excludedCount++;
                    continue;
                }

                CleanupItem item;
                if (entry is FileInfo file)
                {
                    item = new CleanupItem(file.FullName, false, file.Length, file.LastWriteTimeUtc);
                }
                else
                {
                    var (bytes, newest) = FileTree.Measure((DirectoryInfo)entry);
                    item = new CleanupItem(entry.FullName, true, bytes, newest);
                }

                if (category.MinimumAge > TimeSpan.Zero && nowUtc - item.LastWriteUtc < category.MinimumAge)
                    continue;
                items.Add(item);
            }
        }

        items.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return new CategoryScan(category, items, items.Count > 0 ? ScanStatus.Found : ScanStatus.NothingFound)
        {
            Excluded = excludedCount,
        };
    }

    internal static bool Matches(CleanupCategory category, FileSystemInfo entry) => category.Kind switch
    {
        ItemKind.Children => true,
        ItemKind.Folders => entry is DirectoryInfo,
        ItemKind.Files => entry is FileInfo &&
            category.FilePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, entry.Name, ignoreCase: true)),
        _ => false,
    };
}

internal static class FileTree
{
    private static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    public static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Total size of everything below <paramref name="dir"/> and the newest write time found,
    /// without following junctions or symbolic links.
    /// </summary>
    public static (long Bytes, DateTime NewestWriteUtc) Measure(DirectoryInfo dir)
    {
        long bytes = 0;
        DateTime newest = dir.LastWriteTimeUtc;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = current.EnumerateFileSystemInfos("*", OneLevel).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (child.LastWriteTimeUtc > newest) newest = child.LastWriteTimeUtc;
                if (IsReparsePoint(child)) continue;
                if (child is FileInfo f) bytes += f.Length;
                else if (child is DirectoryInfo d) pending.Push(d);
            }
        }
        return (bytes, newest);
    }
}
