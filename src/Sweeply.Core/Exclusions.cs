namespace Sweeply.Core;

/// <summary>Folders and files the user never wants cleaned (the "Never clean" list).</summary>
public static class Exclusions
{
    /// <summary>
    /// True if <paramref name="path"/> is on the list, is inside something on the list, or contains
    /// something on the list (moving a folder would take what is inside it along).
    /// </summary>
    public static bool IsExcluded(string path, IReadOnlyCollection<string>? excluded)
    {
        if (excluded is null || excluded.Count == 0) return false;
        string? p = Normalize(path);
        if (p is null) return true; // a path that can't even be read as one is not something to move
        foreach (string entry in excluded)
        {
            string? x = Normalize(entry);
            if (x is null) continue;
            if (string.Equals(p, x, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            if (x.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
