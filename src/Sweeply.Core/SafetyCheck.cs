namespace Sweeply.Core;

public enum SafetyVerdict
{
    Ok,
    Missing,
    /// <summary>Not a direct child of one of the category's folders, or not the right kind of item.</summary>
    OutsideCategory,
    IsCategoryRoot,
    /// <summary>The item or its category folder is a junction or symbolic link.</summary>
    ReparsePoint,
    NotLocalFixedDrive,
    AppRunning,
    /// <summary>Something inside changed since the scan and is now too new to remove.</summary>
    TooRecent,
    /// <summary>On the "Never clean" list, inside something on it, or contains something on it.</summary>
    Excluded,
}

/// <summary>
/// The second look right before an item is moved to the Recycle Bin. The scan may be minutes old;
/// this re-checks everything against the disk as it is now.
/// </summary>
public static class SafetyCheck
{
    public static SafetyVerdict Check(
        CleanupItem item,
        CleanupCategory category,
        DateTime nowUtc,
        Func<string, bool>? isRunning = null,
        Func<string, bool>? isLocalFixedDrive = null,
        IReadOnlyCollection<string>? excluded = null)
    {
        isRunning ??= Processes.IsRunning;
        isLocalFixedDrive ??= IsLocalFixedDrive;

        string full;
        try { full = Normalize(item.Path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SafetyVerdict.OutsideCategory;
        }

        // Must be a direct child of one of the category's folders.
        string? root = null;
        foreach (string r in category.Roots)
        {
            string nr = Normalize(r);
            if (string.Equals(full, nr, StringComparison.OrdinalIgnoreCase)) return SafetyVerdict.IsCategoryRoot;
            if (string.Equals(Path.GetDirectoryName(full), nr, StringComparison.OrdinalIgnoreCase)) { root = nr; break; }
        }
        if (root is null) return SafetyVerdict.OutsideCategory;

        // The list may have changed since the scan; it always wins.
        if (Exclusions.IsExcluded(full, excluded)) return SafetyVerdict.Excluded;

        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists) return SafetyVerdict.Missing;
        if (!Scanner.Matches(category, info)) return SafetyVerdict.OutsideCategory;

        if (FileTree.IsReparsePoint(info) || FileTree.IsReparsePoint(new DirectoryInfo(root)))
            return SafetyVerdict.ReparsePoint;

        if (!isLocalFixedDrive(full)) return SafetyVerdict.NotLocalFixedDrive;

        foreach (string process in category.BlockingProcesses)
            if (isRunning(process)) return SafetyVerdict.AppRunning;

        if (category.MinimumAge > TimeSpan.Zero)
        {
            DateTime newest = info is DirectoryInfo d ? FileTree.Measure(d).NewestWriteUtc : FileTree.Newest(info);
            if (nowUtc - newest < category.MinimumAge) return SafetyVerdict.TooRecent;
        }

        return SafetyVerdict.Ok;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Only this PC's own disks: no USB sticks, network shares, optical or RAM drives.</summary>
    public static bool IsLocalFixedDrive(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return false; // UNC share
        string? drive = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(drive)) return false;
        try { return new DriveInfo(drive).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { return false; }
    }
}
