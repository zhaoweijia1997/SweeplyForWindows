using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sweeply.Core;

public sealed record RecordedItem(string Path, bool IsDirectory, long Bytes);

public enum UndoState
{
    NotUndone,
    Undone,
    PartlyUndone,
}

/// <summary>One clean: what went to the Recycle Bin and when, so it can be put back.</summary>
public sealed class CleanRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public List<RecordedItem> Items { get; init; } = new();

    /// <summary>Done by "Clean up every day" rather than by hand (older history files: false).</summary>
    public bool Automatic { get; init; }

    public UndoState State { get; set; }
    public int RestoredCount { get; set; }
    public int NotRestoredCount { get; set; }

    [JsonIgnore] public long Bytes => Items.Sum(i => i.Bytes);
}

/// <summary>
/// The last few cleans, newest first, kept in a file of their own (written by the app only,
/// never mixed with settings people might edit).
/// </summary>
public sealed class CleanHistory
{
    public const int Keep = 5;
    private readonly string? _file;

    /// <param name="file">Where to keep the history; null keeps it in memory only.</param>
    public CleanHistory(string? file) => _file = file;

    public List<CleanRecord> Records { get; private set; } = new();

    public static CleanHistory Load(string? file)
    {
        var history = new CleanHistory(file);
        try
        {
            if (file is not null && File.Exists(file))
                history.Records = JsonSerializer.Deserialize<List<CleanRecord>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return history;
    }

    public void Add(CleanRecord record)
    {
        Records.Insert(0, record);
        if (Records.Count > Keep) Records.RemoveRange(Keep, Records.Count - Keep);
        Save();
    }

    public void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>One item in the Recycle Bin: the "$I" file that describes it and the "$R" data next to it.</summary>
public sealed record RecycleBinEntry(string InfoFile, string DataPath, string OriginalPath, DateTime DeletedUtc, long Size);

public static class RecycleBinReader
{
    /// <summary>
    /// Reads the "$I" descriptions in one Recycle Bin folder (such as C:\$Recycle.Bin\&lt;user SID&gt;).
    /// Version 1 (Windows Vista to 8) stores a fixed 260-character path; version 2 (Windows 10 and later)
    /// stores its length first.
    /// </summary>
    public static List<RecycleBinEntry> Read(string folder)
    {
        var entries = new List<RecycleBinEntry>();
        IEnumerable<string> infos;
        try { infos = Directory.EnumerateFiles(folder, "$I*").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return entries; }

        foreach (string info in infos)
        {
            try
            {
                byte[] b = File.ReadAllBytes(info);
                if (b.Length < 28) continue;
                long version = BitConverter.ToInt64(b, 0);
                long size = BitConverter.ToInt64(b, 8);
                DateTime deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(b, 16));
                string path;
                if (version == 2)
                {
                    int chars = BitConverter.ToInt32(b, 24);
                    if (chars <= 0 || 28 + chars * 2 > b.Length) continue;
                    path = Encoding.Unicode.GetString(b, 28, chars * 2).TrimEnd('\0');
                }
                else if (version == 1)
                {
                    int len = Math.Min(520, b.Length - 24);
                    path = Encoding.Unicode.GetString(b, 24, len).Split('\0')[0];
                }
                else continue;
                string data = Path.Combine(folder, "$R" + Path.GetFileName(info)[2..]);
                entries.Add(new RecycleBinEntry(info, data, path, deleted, size));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return entries;
    }

    /// <summary>This user's Recycle Bin folders on this PC's own disks.</summary>
    public static IEnumerable<string> CurrentUserFolders()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) yield break;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            string folder = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(folder)) yield return folder;
        }
    }
}

public enum UndoSkipReason
{
    /// <summary>Not found in the Recycle Bin (emptied, or deleted from there).</summary>
    NotInRecycleBin,
    /// <summary>Something is already at the original place; it is never overwritten.</summary>
    AlreadyExists,
    Failed,
}

public sealed record UndoOutcome(int Restored, IReadOnlyList<(string Path, UndoSkipReason Reason)> Skipped);

public static class Undo
{
    /// <summary>How far outside a clean's start and finish a Recycle Bin entry's time may be.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Puts back what one clean moved. An item matches a Recycle Bin entry only if the original path is
    /// the same and it was deleted during that clean, so nothing else in the Recycle Bin is touched.
    /// Nothing that already exists at the original place is overwritten.
    /// </summary>
    public static UndoOutcome Restore(CleanRecord record, IEnumerable<string> recycleFolders)
    {
        var entries = recycleFolders.SelectMany(RecycleBinReader.Read).ToList();
        DateTime from = record.StartedUtc - Slack, to = record.FinishedUtc + Slack;
        int restored = 0;
        var skipped = new List<(string, UndoSkipReason)>();

        foreach (var item in record.Items)
        {
            var match = entries
                .Where(e => string.Equals(e.OriginalPath, item.Path, StringComparison.OrdinalIgnoreCase)
                            && e.DeletedUtc >= from && e.DeletedUtc <= to
                            && (File.Exists(e.DataPath) || Directory.Exists(e.DataPath)))
                .OrderByDescending(e => e.DeletedUtc)
                .FirstOrDefault();
            bool existsNow = File.Exists(item.Path) || Directory.Exists(item.Path);
            if (match is null)
            {
                // Already back (an earlier undo), or really gone from the Recycle Bin.
                if (!existsNow) skipped.Add((item.Path, UndoSkipReason.NotInRecycleBin));
                continue;
            }
            if (existsNow)
            {
                skipped.Add((item.Path, UndoSkipReason.AlreadyExists));
                continue;
            }
            try
            {
                string? parent = Path.GetDirectoryName(item.Path);
                if (parent is not null) Directory.CreateDirectory(parent);
                if (Directory.Exists(match.DataPath)) Directory.Move(match.DataPath, item.Path);
                else File.Move(match.DataPath, item.Path);
                File.Delete(match.InfoFile);
                entries.Remove(match);
                restored++;
                ShellNotify.Changed(item.Path, isDirectory: Directory.Exists(item.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped.Add((item.Path, UndoSkipReason.Failed));
            }
        }

        record.RestoredCount += restored;
        record.NotRestoredCount = skipped.Count;
        record.State = skipped.Count == 0 ? UndoState.Undone
            : record.RestoredCount > 0 ? UndoState.PartlyUndone
            : record.State;
        return new UndoOutcome(restored, skipped);
    }
}

/// <summary>Tells File Explorer (and the Recycle Bin window) that something reappeared.</summary>
internal static class ShellNotify
{
    private const int SHCNE_CREATE = 0x00000002;
    private const int SHCNE_MKDIR = 0x00000008;
    private const uint SHCNF_PATHW = 0x0005;

    public static void Changed(string path, bool isDirectory)
    {
        IntPtr p = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(isDirectory ? SHCNE_MKDIR : SHCNE_CREATE, SHCNF_PATHW, p, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
