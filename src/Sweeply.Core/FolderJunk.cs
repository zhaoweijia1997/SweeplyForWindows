namespace Sweeply.Core;

public enum FolderCheck
{
    Ok,
    Missing,
    /// <summary>Windows, installed programs or app data: there a "node_modules" or "bin" belongs to a program.</summary>
    SystemFolder,
    /// <summary>A USB stick, network share and so on: only this PC's own disks are cleaned.</summary>
    NotLocalFixedDrive,
}

/// <summary>
/// "Scan this folder" from the folder right-click menu: looks through one folder the user picked for
/// leftovers of programming tools and Office, and turns what it finds into ordinary categories, so the
/// same list, check before moving, Recycle Bin and undo apply.
///
/// A build folder counts only next to the project file that makes it ("bin" and "obj" beside a .csproj,
/// "target" beside Cargo.toml or pom.xml…), so a folder that merely has the same name is never offered.
/// Each category's roots are the folders where something was found, which keeps the check before moving
/// ("a direct child of one of the roots, with a matching name") exactly as strict as for the rest.
/// </summary>
public static class FolderJunk
{
    /// <summary>Gives up after this many folders, so right-clicking a whole drive cannot run for ages.</summary>
    public const int FolderLimit = 200_000;

    /// <summary>The command-line switch the folder right-click menu starts the app with.</summary>
    public const string ScanFolderArg = "--scan-folder";

    /// <summary>
    /// The folder after <see cref="ScanFolderArg"/>, or null. Explorer passes a drive as <c>"C:\"</c>,
    /// which Windows reads as <c>C:"</c> (the backslash escapes the closing quote).
    /// </summary>
    public static string? FolderFromArgs(IReadOnlyList<string> args)
    {
        int i = args.ToList().IndexOf(ScanFolderArg);
        if (i < 0 || i + 1 >= args.Count) return null;
        string folder = args[i + 1].Trim().TrimEnd('"');
        if (folder.Length == 2 && folder[1] == ':') folder += '\\'; // "C:" alone would mean the current folder on C:
        return folder.Length == 0 ? null : folder;
    }

    private sealed record Rule(string Id, ItemKind Kind, string[] Names, Func<DirectoryInfo, bool> ParentIsProject,
        bool SelectedByDefault = true, int MinimumAgeDays = 0);

    private static readonly Rule[] FolderRules =
    {
        // Not ticked: installing again needs the network, and an old project may no longer install the same.
        new("folder-node-modules", ItemKind.Folders, new[] { "node_modules" }, p => Has(p, "package.json"), SelectedByDefault: false),
        // Not ticked: some older projects keep files they need (DLLs nothing builds) right in bin.
        new("folder-dotnet-build", ItemKind.Folders, new[] { "bin", "obj" }, p => HasAny(p, "*.csproj", "*.vbproj", "*.fsproj"),
            SelectedByDefault: false),
        new("folder-target", ItemKind.Folders, new[] { "target" }, p => Has(p, "Cargo.toml") || Has(p, "pom.xml")),
        new("folder-gradle-build", ItemKind.Folders, new[] { "build", ".gradle" },
            p => HasAny(p, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts")),
        new("folder-python-cache", ItemKind.Folders, new[] { "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache" }, _ => true),
    };

    private static readonly Rule[] FileRules =
    {
        // Word, Excel and PowerPoint's "owner" files; one less than a day old may belong to a document still open.
        new("folder-office-temp", ItemKind.Files, new[] { "~$*" }, _ => true, MinimumAgeDays: 1),
        new("folder-temp-files", ItemKind.Files, new[] { "*.tmp", "Thumbs.db", ".DS_Store" }, _ => true, MinimumAgeDays: 1),
    };

    /// <summary>The ids of every category a folder scan can produce, in display order.</summary>
    public static IReadOnlyList<string> CategoryIds { get; } = FolderRules.Concat(FileRules).Select(r => r.Id).ToList();

    /// <summary>
    /// Never looked into: a node_modules that is not a project's own (its packages' nested node_modules
    /// are part of it), and folders Windows owns. Folders whose names start with a dot are not entered
    /// either: version control data, and tool folders such as ".vscode\extensions", where a node_modules
    /// is part of an installed extension.
    /// </summary>
    private static readonly HashSet<string> NeverEnter = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "AppData", "$Recycle.Bin", "System Volume Information",
    };

    private static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0, // Office owner files and Thumbs.db are hidden
    };

    /// <summary>The folders Windows itself, installed programs and app data live in.</summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        return roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether <paramref name="folder"/> may be scanned at all.</summary>
    public static FolderCheck Check(string folder, IReadOnlyCollection<string> systemFolders, Func<string, bool>? isLocalFixedDrive = null)
    {
        isLocalFixedDrive ??= SafetyCheck.IsLocalFixedDrive;
        string full;
        try { full = Normalize(folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return FolderCheck.Missing; }
        if (!Directory.Exists(full)) return FolderCheck.Missing;
        if (systemFolders.Any(s => IsSameOrInside(full, s))) return FolderCheck.SystemFolder;
        if (!isLocalFixedDrive(full)) return FolderCheck.NotLocalFixedDrive;
        return FolderCheck.Ok;
    }

    /// <summary>
    /// Looks through <paramref name="folder"/> and returns one category per kind of leftover, each rooted
    /// at the folders where it was found (no roots when none was). Only reads.
    /// </summary>
    /// <param name="systemFolders">Never entered, e.g. "C:\Windows" when a whole drive is scanned.</param>
    /// <param name="truncated">True when <see cref="FolderLimit"/> was reached before the end.</param>
    public static IReadOnlyList<CleanupCategory> Find(string folder, IReadOnlyCollection<string> systemFolders,
        out bool truncated, CancellationToken cancel = default)
    {
        var roots = CategoryIds.ToDictionary(id => id, _ => new List<string>());
        truncated = false;
        var start = new DirectoryInfo(Normalize(folder));
        if (start.Exists && !FileTree.IsReparsePoint(start))
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(start);
            int visited = 0;
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (++visited > FolderLimit) { truncated = true; break; }
                var dir = pending.Pop();

                List<FileSystemInfo> children;
                try { children = dir.EnumerateFileSystemInfos("*", OneLevel).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                foreach (var rule in FileRules)
                    if (children.Any(c => c is FileInfo && !FileTree.IsReparsePoint(c) && NameMatches(rule, c.Name)))
                        roots[rule.Id].Add(dir.FullName);

                // Pushed in reverse so folders are looked at in name order (keeps the lists stable).
                var next = new List<DirectoryInfo>();
                foreach (var child in children.OfType<DirectoryInfo>())
                {
                    if (FileTree.IsReparsePoint(child)) continue; // never follow links
                    var rule = FolderRules.FirstOrDefault(r => NameMatches(r, child.Name));
                    if (rule is not null && rule.ParentIsProject(dir))
                    {
                        if (!roots[rule.Id].Contains(dir.FullName)) roots[rule.Id].Add(dir.FullName);
                        continue; // never look inside a leftover for more
                    }
                    if (child.Name.StartsWith('.') || NeverEnter.Contains(child.Name)) continue;
                    if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
                        continue; // protected Windows folders such as "Recovery" or "$WINDOWS.~BT"
                    if (systemFolders.Any(s => IsSameOrInside(child.FullName, s))) continue;
                    next.Add(child);
                }
                foreach (var child in next.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)) pending.Push(child);
            }
        }

        return FolderRules.Concat(FileRules).Select(r => new CleanupCategory
        {
            Id = r.Id,
            Group = CategoryGroup.Folder,
            Roots = roots[r.Id],
            Kind = r.Kind,
            NamePatterns = r.Names,
            MinimumAge = TimeSpan.FromDays(r.MinimumAgeDays),
            SelectedByDefault = r.SelectedByDefault,
        }).ToList();
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        return full.EndsWith(':') ? full + '\\' : full; // "D:" alone would mean the current folder on D:
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        string f = Path.GetFullPath(folder).TrimEnd('\\', '/');
        string p = Path.GetFullPath(path).TrimEnd('\\', '/');
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NameMatches(Rule rule, string name) =>
        rule.Names.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));

    private static bool Has(DirectoryInfo dir, string file) => File.Exists(Path.Combine(dir.FullName, file));

    private static bool HasAny(DirectoryInfo dir, params string[] patterns)
    {
        try { return patterns.Any(p => dir.EnumerateFiles(p).Any()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
