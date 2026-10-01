namespace Sweeply.Core;

/// <summary>
/// Where WeCom (企业微信, process "WXWork") keeps what it downloads and writes. Only caches, logs, chat
/// pictures and videos, and received files are returned. Never among them: the chat database ("Data"),
/// the search index ("Index"), backups, stickers, files synced with WeDrive, and the app's own components.
/// </summary>
internal static class WeComFolders
{
    /// <summary>While it runs, nothing of WeCom's is scanned or moved.</summary>
    public static readonly string[] Processes = { "WXWork" };

    private static string StorageRoot(KnownPaths p) => Path.Combine(p.DocumentsFolder, "WXWork");

    /// <summary>Folders inside an account folder that hold people's things, never searched for caches.</summary>
    private static readonly HashSet<string> AccountData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Data", "Index", "Backup", "Emotion", "WeDrive",
    };

    /// <summary>Temporary files of chat pictures, videos and files, and the caches of the built-in browser.</summary>
    public static IReadOnlyList<string> CacheRoots(KnownPaths p)
    {
        var roots = new List<string>();
        foreach (string account in Accounts(p))
        {
            foreach (string kind in new[] { "File", "Image", "Video" })
                roots.Add(Path.Combine(account, "Cache", kind, "Temp"));
            // The built-in browser keeps a profile in a folder of its own inside the account folder.
            foreach (string sub in SubFolders(account).Where(s => !AccountData.Contains(Path.GetFileName(s))))
                roots.AddRange(ChromiumCaches.Find(sub, 2));
        }
        string storage = StorageRoot(p);
        roots.AddRange(ChromiumCaches.Find(Path.Combine(storage, "qtCef"), 1));
        roots.Add(Path.Combine(storage, "qtCef", "Service Worker", "CacheStorage"));
        roots.Add(Path.Combine(storage, "component_crx_cache"));
        return roots;
    }

    /// <summary>Folders holding WeCom's own .log / .xlog files.</summary>
    public static IReadOnlyList<string> LogRoots(KnownPaths p)
    {
        string log = Path.Combine(p.Roaming, "Tencent", "WXWork", "Log");
        if (!Directory.Exists(log)) return Array.Empty<string>();
        return new[] { log }.Concat(SubFolders(log)).ToList();
    }

    /// <summary>Folders whose month folders ("2025-03") hold chat pictures and videos.</summary>
    public static IReadOnlyList<string> MediaRoots(KnownPaths p) =>
        Accounts(p).SelectMany(a => new[] { Path.Combine(a, "Cache", "Image"), Path.Combine(a, "Cache", "Video") }).ToList();

    /// <summary>Month folders holding received files; the files inside are offered one by one.</summary>
    public static IReadOnlyList<string> ReceivedFileRoots(KnownPaths p) =>
        Accounts(p).SelectMany(a => SubFolders(Path.Combine(a, "Cache", "File")).Where(f => IsMonth(Path.GetFileName(f)))).ToList();

    /// <summary>Account folders: they hold both the chat database ("Data") and the downloaded files ("Cache").</summary>
    private static IEnumerable<string> Accounts(KnownPaths p) =>
        SubFolders(StorageRoot(p)).Where(a => Directory.Exists(Path.Combine(a, "Data")) && Directory.Exists(Path.Combine(a, "Cache")));

    private static IEnumerable<string> SubFolders(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            return dir.Exists
                ? dir.EnumerateDirectories().Where(d => !FileTree.IsReparsePoint(d)).Select(d => d.FullName).ToList()
                : Enumerable.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }

    /// <summary>"2025-03".</summary>
    private static bool IsMonth(string name) =>
        name.Length == 7 && name[4] == '-' && name.Remove(4, 1).All(char.IsAsciiDigit);
}
