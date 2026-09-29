using System.Text;

namespace Sweeply.Core.Tests;

/// <summary>Undo against a pretend Recycle Bin folder built the way Windows builds one ($I description + $R data).</summary>
public class UndoTests
{
    private static readonly DateTime CleanStart = new(2026, 9, 29, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>Moves <paramref name="original"/> into the pretend bin as Windows would, and writes its $I file.</summary>
    private static void Recycle(string bin, string original, string id, DateTime deletedUtc, int version = 2)
    {
        string data = Path.Combine(bin, "$R" + id);
        if (Directory.Exists(original)) Directory.Move(original, data);
        else File.Move(original, data);
        File.WriteAllBytes(Path.Combine(bin, "$I" + id), Info(original, deletedUtc, version));
    }

    private static byte[] Info(string original, DateTime deletedUtc, int version)
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes((long)version));
        b.AddRange(BitConverter.GetBytes(1234L));
        b.AddRange(BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()));
        if (version == 2)
        {
            b.AddRange(BitConverter.GetBytes(original.Length + 1));
            b.AddRange(Encoding.Unicode.GetBytes(original + "\0"));
        }
        else
        {
            var path = new byte[520];
            Encoding.Unicode.GetBytes(original).CopyTo(path, 0);
            b.AddRange(path);
        }
        return b.ToArray();
    }

    private static CleanRecord Record(params (string Path, bool Dir)[] items) => new()
    {
        StartedUtc = CleanStart,
        FinishedUtc = CleanStart.AddSeconds(20),
        Items = items.Select(i => new RecordedItem(i.Path, i.Dir, 16)).ToList(),
    };

    [Fact]
    public void Reads_both_description_formats()
    {
        using var t = new TestFolder();
        string bin = t.Dir("bin");
        string a = t.File("a.tmp", 16), b = t.File("b.tmp", 16);
        Recycle(bin, a, "AAA.tmp", CleanStart, version: 2);
        Recycle(bin, b, "BBB.tmp", CleanStart, version: 1);

        var entries = RecycleBinReader.Read(bin).OrderBy(e => e.OriginalPath).ToList();
        Assert.Equal(new[] { a, b }, entries.Select(e => e.OriginalPath));
        Assert.All(entries, e => Assert.Equal(CleanStart, e.DeletedUtc));
        Assert.EndsWith("$RAAA.tmp", entries[0].DataPath);
    }

    [Fact]
    public void Puts_files_and_folders_back_and_removes_their_bin_entries()
    {
        using var t = new TestFolder();
        string bin = t.Dir("bin");
        string file = t.File(@"place\cache.bin", 16);
        string folder = t.Dir("place", "Cache");
        t.File(@"place\Cache\inside.dat", 16);
        Recycle(bin, file, "F1.bin", CleanStart.AddSeconds(3));
        Recycle(bin, folder, "D1", CleanStart.AddSeconds(4));

        var record = Record((file, false), (folder, true));
        var outcome = Undo.Restore(record, new[] { bin });

        Assert.Equal(2, outcome.Restored);
        Assert.Empty(outcome.Skipped);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(folder, "inside.dat")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(bin));
        Assert.Equal(UndoState.Undone, record.State);
    }

    [Fact]
    public void Never_overwrites_something_already_at_the_original_place()
    {
        using var t = new TestFolder();
        string bin = t.Dir("bin");
        string file = t.File("keep.txt", 16);
        Recycle(bin, file, "K1.txt", CleanStart.AddSeconds(1));
        t.File("keep.txt", 99); // a new file with the same name appeared since

        var record = Record((file, false));
        var outcome = Undo.Restore(record, new[] { bin });

        Assert.Equal(0, outcome.Restored);
        Assert.Equal(UndoSkipReason.AlreadyExists, Assert.Single(outcome.Skipped).Reason);
        Assert.Equal(99, new FileInfo(file).Length);
        Assert.True(File.Exists(Path.Combine(bin, "$RK1.txt")));
    }

    [Fact]
    public void Only_takes_the_entry_deleted_during_that_clean()
    {
        using var t = new TestFolder();
        string bin = t.Dir("bin");
        string path = Path.Combine(t.Root, "same-name.log");

        t.File("same-name.log", 10);
        Recycle(bin, path, "OLD.log", CleanStart.AddDays(-3)); // an older deletion of the same path
        t.File("same-name.log", 20);
        Recycle(bin, path, "NEW.log", CleanStart.AddSeconds(5));

        var outcome = Undo.Restore(Record((path, false)), new[] { bin });

        Assert.Equal(1, outcome.Restored);
        Assert.Equal(20, new FileInfo(path).Length);
        Assert.True(File.Exists(Path.Combine(bin, "$ROLD.log")), "the older entry is left alone");
    }

    [Fact]
    public void Reports_items_no_longer_in_the_bin_and_a_second_undo_is_harmless()
    {
        using var t = new TestFolder();
        string bin = t.Dir("bin");
        string a = t.File("a.tmp", 16);
        string gone = Path.Combine(t.Root, "emptied.tmp");
        Recycle(bin, a, "A.tmp", CleanStart);

        var record = Record((a, false), (gone, false));
        var first = Undo.Restore(record, new[] { bin });
        Assert.Equal(1, first.Restored);
        Assert.Equal(UndoSkipReason.NotInRecycleBin, Assert.Single(first.Skipped).Reason);
        Assert.Equal(UndoState.PartlyUndone, record.State);

        var second = Undo.Restore(record, new[] { bin });
        Assert.Equal(0, second.Restored);
        Assert.Single(second.Skipped); // only the one that really is gone; "a" is simply already back
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void History_keeps_the_last_five_newest_first_and_survives_a_reload()
    {
        using var t = new TestFolder();
        string file = Path.Combine(t.Root, "history.json");
        var history = CleanHistory.Load(file);
        for (int i = 0; i < 7; i++)
            history.Add(new CleanRecord { StartedUtc = CleanStart.AddMinutes(i), FinishedUtc = CleanStart.AddMinutes(i) });

        var again = CleanHistory.Load(file);
        Assert.Equal(CleanHistory.Keep, again.Records.Count);
        Assert.Equal(CleanStart.AddMinutes(6), again.Records[0].StartedUtc);
        Assert.Equal(CleanStart.AddMinutes(2), again.Records[^1].StartedUtc);
    }

    [Fact]
    public void A_damaged_history_file_is_treated_as_empty()
    {
        using var t = new TestFolder();
        string file = t.File("history.json", 0);
        File.WriteAllText(file, "{ not json");
        Assert.Empty(CleanHistory.Load(file).Records);
    }
}
