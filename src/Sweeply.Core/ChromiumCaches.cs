namespace Sweeply.Core;

/// <summary>
/// Finds the disk caches of apps built on Chromium (Electron, CEF, WebView2): QQ, DingTalk, Feishu,
/// Steam, Discord and so on. A folder counts only when it looks like a Chromium cache inside, not just by
/// its name, so an app's own folder that happens to be called "Cache" is never taken for one:
/// <list type="bullet">
/// <item>"Cache" holding "Cache_Data\index", or "index" + "data_0" (the older block-file format)</item>
/// <item>"GPUCache", "GrShaderCache", "DawnWebGPUCache", "DawnGraphiteCache" holding "index" + "data_0"</item>
/// <item>"Code Cache" holding "js" or "wasm" folders</item>
/// </list>
/// Everything inside such a folder is rebuilt by the app as needed.
/// </summary>
public static class ChromiumCaches
{
    private static readonly string[] BlockFileCaches = { "GPUCache", "GrShaderCache", "DawnWebGPUCache", "DawnGraphiteCache" };

    /// <summary>Folders never searched: people's data and app state, not caches.</summary>
    private static readonly HashSet<string> NotSearched = new(StringComparer.OrdinalIgnoreCase)
    {
        "User", "Local Storage", "Session Storage", "IndexedDB", "databases", "blob_storage", "WebStorage",
        "Extensions", "Backups", "File System", "Storage", "node_modules", "WeDrive",
    };

    /// <summary>The cache folders under <paramref name="root"/>, looking at most <paramref name="depth"/> levels down.</summary>
    public static IReadOnlyList<string> Find(string root, int depth = 4)
    {
        var found = new List<string>();
        var dir = new DirectoryInfo(root);
        if (dir.Exists && !FileTree.IsReparsePoint(dir)) Walk(dir, depth, found);
        return found;
    }

    /// <summary>Same as <see cref="Find"/> for several roots.</summary>
    public static IReadOnlyList<string> FindAll(IEnumerable<string> roots, int depth = 4) =>
        roots.SelectMany(r => Find(r, depth)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsCache(DirectoryInfo dir)
    {
        string name = dir.Name;
        if (name.Equals("Cache", StringComparison.OrdinalIgnoreCase))
            return File.Exists(Path.Combine(dir.FullName, "Cache_Data", "index")) || HasBlockFiles(dir.FullName);
        if (BlockFileCaches.Contains(name, StringComparer.OrdinalIgnoreCase))
            return HasBlockFiles(dir.FullName);
        if (name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase))
            return Directory.Exists(Path.Combine(dir.FullName, "js")) || Directory.Exists(Path.Combine(dir.FullName, "wasm"));
        return false;
    }

    private static bool HasBlockFiles(string dir) =>
        File.Exists(Path.Combine(dir, "index")) && File.Exists(Path.Combine(dir, "data_0"));

    private static void Walk(DirectoryInfo dir, int depth, List<string> found)
    {
        IEnumerable<DirectoryInfo> children;
        try { children = dir.EnumerateDirectories().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var child in children)
        {
            if (FileTree.IsReparsePoint(child) || NotSearched.Contains(child.Name)) continue;
            if (IsCache(child))
            {
                found.Add(child.FullName); // never look inside a cache for more caches
                continue;
            }
            if (depth > 1) Walk(child, depth - 1, found);
        }
    }
}
