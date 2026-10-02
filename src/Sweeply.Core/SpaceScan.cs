using System.Diagnostics;
using System.IO.Enumeration;

namespace Sweeply.Core;

/// <summary>What a file is, by its extension, for the "by type" totals.</summary>
public enum FileKind
{
    Videos,
    Music,
    Pictures,
    Documents,
    Archives,
    DiskImages,
    Programs,
    Other,
}

/// <summary>
/// One folder of a space analysis. Only folders are kept (a drive can hold millions of files); the files
/// directly inside one are read again when it is opened (<see cref="SpaceScanner.FilesIn"/>).
/// </summary>
public sealed class SpaceFolder
{
    internal SpaceFolder(SpaceFolder? parent, string name)
    {
        Parent = parent;
        Name = name;
    }

    public SpaceFolder? Parent { get; }
    public string Name { get; }
    public string Path => Parent is null ? Name : System.IO.Path.Combine(Parent.Path, Name);

    /// <summary>Everything below, files and folders.</summary>
    public long Bytes { get; internal set; }

    /// <summary>The files directly inside (not in subfolders).</summary>
    public long OwnFileBytes { get; internal set; }

    /// <summary>Files below, at any depth.</summary>
    public long FileCount { get; internal set; }

    internal long OwnFileCount { get; set; }

    /// <summary>Subfolders, largest first.</summary>
    public List<SpaceFolder> Folders { get; } = new();

    /// <summary>This folder or one below could not be read (no permission): there is at least this much.</summary>
    public bool Incomplete { get; internal set; }

    /// <summary>A junction or symbolic link: it points somewhere else, so it is neither entered nor counted.</summary>
    public bool IsLink { get; internal init; }

    /// <summary>Takes away something that was moved: from the totals of this folder and every folder above.</summary>
    public void Subtract(long bytes, long files)
    {
        for (var f = this; f is not null; f = f.Parent)
        {
            f.Bytes = Math.Max(0, f.Bytes - bytes);
            f.FileCount = Math.Max(0, f.FileCount - files);
        }
    }
}

public sealed record SpaceFile(string Path, long Bytes, DateTime LastWriteUtc)
{
    public FileKind Kind => SpaceScanner.KindOf(Path);
}

public sealed record SpaceKindTotal(FileKind Kind, long Bytes, long Count);

public sealed class SpaceResult
{
    public required SpaceFolder Root { get; init; }

    /// <summary>The <see cref="SpaceScanner.LargestCount"/> largest files, largest first.</summary>
    public required List<SpaceFile> LargestFiles { get; init; }

    /// <summary>Totals per kind of file, largest first.</summary>
    public required List<SpaceKindTotal> ByKind { get; init; }

    public long FolderCount { get; init; }

    /// <summary>Folders that could not be read: what they hold is not counted.</summary>
    public int UnreadableFolders { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>The folder at <paramref name="path"/>, or null if it is not in this result.</summary>
    public SpaceFolder? Find(string path)
    {
        string root = Root.Path.TrimEnd('\\');
        string p = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) return Root;
        if (!p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var folder = Root;
        foreach (string part in p[(root.Length + 1)..].Split('\\'))
        {
            folder = folder.Folders.FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
            if (folder is null) return null;
        }
        return folder;
    }
}

public readonly record struct SpaceProgress(long Folders, long Files, long Bytes, string Current);

/// <summary>Finds out what takes up the space on a drive or in a folder. Only reads.</summary>
public static class SpaceScanner
{
    public const int LargestCount = 100;

    private static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = false, // a folder that can't be read is reported as incomplete, not silently empty
        RecurseSubdirectories = false,
        AttributesToSkip = 0,       // hidden files take space too
        ReturnSpecialDirectories = false,
    };

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime LastWriteUtc);

    private static FileSystemEnumerable<Entry> Enumerate(string folder) =>
        new(folder, (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes,
            e.LastWriteTimeUtc.UtcDateTime), OneLevel);

    public static SpaceResult Scan(string folder, IProgress<SpaceProgress>? progress = null, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        var root = new SpaceFolder(null, Normalize(folder));
        var all = new List<SpaceFolder> { root };
        var pending = new Stack<SpaceFolder>();
        pending.Push(root);
        var largest = new PriorityQueue<SpaceFile, long>(); // smallest of the largest on top
        var kindBytes = new long[Enum.GetValues<FileKind>().Length];
        var kindCount = new long[kindBytes.Length];
        long files = 0, bytes = 0, lastReport = 0;
        int unreadable = 0;

        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var current = pending.Pop();
            string path = current.Path;
            try
            {
                foreach (var e in Enumerate(path))
                {
                    if (e.IsDirectory)
                    {
                        bool link = (e.Attributes & FileAttributes.ReparsePoint) != 0;
                        var child = new SpaceFolder(current, e.Name) { IsLink = link };
                        current.Folders.Add(child);
                        all.Add(child);
                        if (!link) pending.Push(child);
                        continue;
                    }
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue; // a link to a file: its data is elsewhere

                    current.OwnFileBytes += e.Length;
                    current.OwnFileCount++;
                    files++;
                    bytes += e.Length;
                    int k = (int)KindOf(e.Name);
                    kindBytes[k] += e.Length;
                    kindCount[k]++;
                    if (largest.Count < LargestCount)
                        largest.Enqueue(new SpaceFile(System.IO.Path.Combine(path, e.Name), e.Length, e.LastWriteUtc), e.Length);
                    else if (e.Length > largest.Peek().Bytes)
                        largest.EnqueueDequeue(new SpaceFile(System.IO.Path.Combine(path, e.Name), e.Length, e.LastWriteUtc), e.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                current.Incomplete = true;
                unreadable++;
            }

            if (progress is not null && clock.ElapsedMilliseconds - lastReport >= 100)
            {
                lastReport = clock.ElapsedMilliseconds;
                progress.Report(new SpaceProgress(all.Count, files, bytes, path));
            }
        }

        // Totals from the bottom up: every folder was added after the one it is in.
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var f = all[i];
            f.Bytes += f.OwnFileBytes;
            f.FileCount += f.OwnFileCount;
            if (f.Parent is { } parent)
            {
                parent.Bytes += f.Bytes;
                parent.FileCount += f.FileCount;
                parent.Incomplete |= f.Incomplete;
            }
        }
        foreach (var f in all)
            if (f.Folders.Count > 1) f.Folders.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        var top = new List<SpaceFile>(largest.Count);
        while (largest.Count > 0) top.Add(largest.Dequeue());
        top.Reverse();

        var byKind = Enum.GetValues<FileKind>()
            .Select(k => new SpaceKindTotal(k, kindBytes[(int)k], kindCount[(int)k]))
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Bytes)
            .ToList();

        progress?.Report(new SpaceProgress(all.Count, files, bytes, root.Path));
        return new SpaceResult
        {
            Root = root,
            LargestFiles = top,
            ByKind = byKind,
            FolderCount = all.Count,
            UnreadableFolders = unreadable,
            Duration = clock.Elapsed,
        };
    }

    /// <summary>The files directly inside <paramref name="folder"/>, largest first; empty if it can't be read.</summary>
    public static List<SpaceFile> FilesIn(string folder)
    {
        var list = new List<SpaceFile>();
        try
        {
            foreach (var e in Enumerate(folder))
            {
                if (e.IsDirectory || (e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                list.Add(new SpaceFile(System.IO.Path.Combine(folder, e.Name), e.Length, e.LastWriteUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    private static readonly Dictionary<string, FileKind> Kinds = BuildKinds();

    private static Dictionary<string, FileKind> BuildKinds()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string extensions)
        {
            foreach (string ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map["." + ext] = kind;
        }
        Add(FileKind.Videos, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts mts 3gp vob rmvb rm");
        Add(FileKind.Music, "mp3 flac wav aac m4a ogg oga opus wma ape aiff aif mid midi");
        Add(FileKind.Pictures, "jpg jpeg png gif bmp tif tiff webp heic heif avif raw cr2 cr3 nef arw dng orf rw2 psd svg ico");
        Add(FileKind.Documents, "pdf doc docx xls xlsx xlsm ppt pptx txt md csv rtf odt ods odp epub mobi");
        Add(FileKind.Archives, "zip rar 7z tar gz tgz bz2 xz zst cab lz lzma");
        Add(FileKind.DiskImages, "iso img vhd vhdx vmdk vdi qcow2 wim esd dmg");
        Add(FileKind.Programs, "exe dll sys msi msix msixbundle appx appxbundle ocx drv");
        return map;
    }

    public static FileKind KindOf(string fileName) =>
        Kinds.TryGetValue(System.IO.Path.GetExtension(fileName), out var kind) ? kind : FileKind.Other;

    private static string Normalize(string folder)
    {
        string full = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/');
        return full.EndsWith(':') ? full + '\\' : full; // "D:" alone would mean the current folder on D:
    }
}

/// <summary>Why something found on the Space page can or can't be moved to the Recycle Bin from there.</summary>
public enum SpaceMoveCheck
{
    Ok,
    /// <summary>Windows, installed programs or app data, or a folder that holds them.</summary>
    SystemOrAppData,
    /// <summary>Desktop, Documents, Downloads…: what is inside may go, the folder itself stays.</summary>
    PersonalFolder,
    /// <summary>pagefile.sys, System Volume Information, $Recycle.Bin and the like.</summary>
    HiddenSystem,
    /// <summary>A junction or symbolic link.</summary>
    Link,
    /// <summary>On the "Never clean" list.</summary>
    NeverClean,
    /// <summary>A USB stick, network drive and so on, or a whole drive.</summary>
    NotHere,
}

/// <summary>
/// The rules for moving things picked by hand on the Space page. Each item becomes a category of its own
/// whose only root is the folder it is in, so the usual check right before moving applies, together
/// with <see cref="CleanupCategory.ProtectedPlaces"/>, <see cref="CleanupCategory.KeptFolders"/> and
/// <see cref="CleanupCategory.RefuseSystemItems"/>.
/// </summary>
public sealed class SpaceMoves
{
    public SpaceMoves(IReadOnlyList<string> protectedPlaces, IReadOnlyList<string> keptFolders)
    {
        ProtectedPlaces = protectedPlaces;
        KeptFolders = keptFolders;
    }

    public IReadOnlyList<string> ProtectedPlaces { get; }
    public IReadOnlyList<string> KeptFolders { get; }

    /// <summary>This PC's Windows, program, program-data and app-data folders, and the user's own folders.</summary>
    public static SpaceMoves ForThisPc(KnownPaths paths)
    {
        var protectedPlaces = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(paths.UserProfile, "AppData"),
            paths.LocalAppData,
            paths.Roaming,
        };
        var kept = new[]
        {
            paths.UserProfile,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            paths.DocumentsFolder,
            paths.Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
        };
        return new SpaceMoves(Clean(protectedPlaces), Clean(kept));

        static IReadOnlyList<string> Clean(IEnumerable<string> list) =>
            list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether <paramref name="path"/> may be offered for moving at all (shown as a tick box or not).</summary>
    public SpaceMoveCheck Check(string path, IReadOnlyCollection<string>? excluded, Func<string, bool>? isLocalFixedDrive = null)
    {
        isLocalFixedDrive ??= SafetyCheck.IsLocalFixedDrive;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return SpaceMoveCheck.NotHere; }
        if (full.EndsWith(':') || Path.GetDirectoryName(full) is null) return SpaceMoveCheck.NotHere; // a whole drive
        if (!isLocalFixedDrive(full)) return SpaceMoveCheck.NotHere;
        if (Exclusions.IsExcluded(full, ProtectedPlaces)) return SpaceMoveCheck.SystemOrAppData;
        if (KeptFolders.Any(k => string.Equals(full, Path.GetFullPath(k).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            return SpaceMoveCheck.PersonalFolder;
        if (Exclusions.IsExcluded(full, excluded)) return SpaceMoveCheck.NeverClean;
        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return SpaceMoveCheck.Link;
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
                return SpaceMoveCheck.HiddenSystem;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SpaceMoveCheck.NotHere; }
        return SpaceMoveCheck.Ok;
    }

    /// <summary>A category of one: rooted at the folder the item is in, with every rule of the Space page.</summary>
    public CleanupCategory CategoryFor(string path) => new()
    {
        Id = "space",
        Group = CategoryGroup.Space,
        Roots = new[] { Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/'))! },
        Kind = ItemKind.Children,
        ProtectedPlaces = ProtectedPlaces,
        KeptFolders = KeptFolders,
        RefuseSystemItems = true,
    };
}
