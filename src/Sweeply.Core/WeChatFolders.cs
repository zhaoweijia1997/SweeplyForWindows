using System.Text;

namespace Sweeply.Core;

/// <summary>
/// Where WeChat keeps what it downloads and writes, for the current app (4.x, process "Weixin")
/// and the older one (3.x, process "WeChat"). Only caches, logs, chat pictures and videos, and
/// received files are returned. Never among them: the chat database (4.x db_storage, 3.x Msg),
/// chat backups, favourites, stickers, settings, and the app's own components (XPlugin).
/// </summary>
internal static class WeChatFolders
{
    /// <summary>While either runs, nothing of WeChat's is scanned or moved.</summary>
    public static readonly string[] Processes = { "Weixin", "WeChat" };

    /// <summary>Cached and temporary files, and the web caches of the built-in browser and mini programs.</summary>
    public static IReadOnlyList<string> CacheRoots(KnownPaths p)
    {
        var roots = new List<string>();
        foreach (string account in Accounts4(p))
        {
            roots.Add(Path.Combine(account, "cache")); // one folder per month
            roots.Add(Path.Combine(account, "temp"));
        }
        foreach (string account in Accounts3(p))
        {
            roots.Add(Path.Combine(account, "FileStorage", "Cache"));
            roots.Add(Path.Combine(account, "FileStorage", "Temp"));
        }
        foreach (string app in AppDataFolders(p))
        {
            foreach (string profile in SubFolders(Path.Combine(app, "radium", "web", "profiles")))
            {
                roots.Add(Path.Combine(profile, "Cache"));
                roots.Add(Path.Combine(profile, "Code Cache"));
                roots.Add(Path.Combine(profile, "GPUCache"));
                roots.Add(Path.Combine(profile, "DawnWebGPUCache"));
                roots.Add(Path.Combine(profile, "DawnGraphiteCache"));
            }
        }
        return roots;
    }

    /// <summary>Folders holding WeChat's own .xlog files.</summary>
    public static IReadOnlyList<string> LogRoots(KnownPaths p)
    {
        var roots = new List<string>();
        foreach (string app in AppDataFolders(p))
        {
            string log = Path.Combine(app, "log");
            if (!Directory.Exists(log)) continue;
            roots.Add(log);
            roots.AddRange(SubFolders(log)); // radium, xplayer, ...
        }
        return roots;
    }

    /// <summary>Folders whose month folders ("2025-03") hold chat pictures and videos.</summary>
    public static IReadOnlyList<string> MediaRoots(KnownPaths p)
    {
        var roots = new List<string>();
        foreach (string account in Accounts4(p))
        {
            roots.AddRange(SubFolders(Path.Combine(account, "msg", "attach"))); // one folder per chat
            roots.Add(Path.Combine(account, "msg", "video"));
        }
        foreach (string account in Accounts3(p))
        {
            string storage = Path.Combine(account, "FileStorage");
            roots.Add(Path.Combine(storage, "Image"));
            roots.Add(Path.Combine(storage, "Video"));
            foreach (string chat in SubFolders(Path.Combine(storage, "MsgAttach")))
            {
                roots.Add(Path.Combine(chat, "Image"));
                roots.Add(Path.Combine(chat, "Thumb"));
            }
        }
        return roots;
    }

    /// <summary>Month folders holding files received in chats; the files inside are offered one by one.</summary>
    public static IReadOnlyList<string> ReceivedFileRoots(KnownPaths p)
    {
        var roots = new List<string>();
        foreach (string account in Accounts4(p))
            roots.AddRange(MonthFolders(Path.Combine(account, "msg", "file")));
        foreach (string account in Accounts3(p))
        {
            string storage = Path.Combine(account, "FileStorage");
            roots.AddRange(MonthFolders(Path.Combine(storage, "File")));
            foreach (string chat in SubFolders(Path.Combine(storage, "MsgAttach")))
                roots.AddRange(MonthFolders(Path.Combine(chat, "File")));
        }
        return roots;
    }

    /// <summary>4.x: account folders in xwechat_files, recognised by the chat database folder beside the files.</summary>
    private static IEnumerable<string> Accounts4(KnownPaths p) =>
        StorageFolders(p, Path.Combine(p.Roaming, "Tencent", "xwechat", "config"), "xwechat_files")
            .SelectMany(SubFolders)
            .Where(a => Directory.Exists(Path.Combine(a, "db_storage")));

    /// <summary>3.x: account folders in "WeChat Files", recognised by their FileStorage folder.</summary>
    private static IEnumerable<string> Accounts3(KnownPaths p) =>
        StorageFolders(p, Path.Combine(p.Roaming, "Tencent", "WeChat", "All Users", "config"), "WeChat Files")
            .SelectMany(SubFolders)
            .Where(a => Directory.Exists(Path.Combine(a, "FileStorage")));

    private static IEnumerable<string> AppDataFolders(KnownPaths p) =>
        new[] { Path.Combine(p.Roaming, "Tencent", "xwechat"), Path.Combine(p.Roaming, "Tencent", "WeChat") }
            .Where(Directory.Exists);

    /// <summary>
    /// Where WeChat stores chat files: Documents, or wherever the user moved them in WeChat's settings.
    /// WeChat writes that choice to a short .ini file in its config folder: "MyDocument:" for Documents,
    /// otherwise the folder it was moved to.
    /// </summary>
    private static IReadOnlyList<string> StorageFolders(KnownPaths p, string configFolder, string folderName)
    {
        var parents = new List<string> { p.DocumentsFolder };
        foreach (string ini in Files(configFolder, "*.ini"))
        {
            string? text = ReadShortText(ini);
            if (text is not null && text != "MyDocument:" && Path.IsPathFullyQualified(text)) parents.Add(text);
        }

        var found = new List<string>();
        foreach (string parent in parents)
        {
            string folder;
            try
            {
                string trimmed = parent.TrimEnd('\\', '/');
                folder = Path.GetFullPath(string.Equals(Path.GetFileName(trimmed), folderName, StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : Path.Combine(parent, folderName));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (Directory.Exists(folder) && !found.Contains(folder, StringComparer.OrdinalIgnoreCase)) found.Add(folder);
        }
        return found;
    }

    /// <summary>The text of a small file (a path, at most), as UTF-8 or else as Chinese ANSI (GBK).</summary>
    private static string? ReadShortText(string file)
    {
        byte[] bytes;
        try
        {
            if (new FileInfo(file).Length > 1024) return null;
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = CodePagesEncodingProvider.Instance.GetEncoding(936)!.GetString(bytes); }
        return text.Trim('﻿', '\0', ' ', '\t', '\r', '\n');
    }

    /// <summary>Direct sub-folders, never following junctions or symbolic links.</summary>
    private static IEnumerable<string> SubFolders(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).Exists
                ? new DirectoryInfo(folder).EnumerateDirectories().Where(d => !FileTree.IsReparsePoint(d)).Select(d => d.FullName).ToList()
                : Enumerable.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }

    private static IEnumerable<string> MonthFolders(string folder) => SubFolders(folder).Where(f => IsMonth(Path.GetFileName(f)));

    /// <summary>"2025-03".</summary>
    private static bool IsMonth(string name) =>
        name.Length == 7 && name[4] == '-' && name.Remove(4, 1).All(char.IsAsciiDigit);

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern).ToList() : Enumerable.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }
}
