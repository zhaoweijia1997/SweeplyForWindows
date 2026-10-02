namespace Sweeply.Core.Tests;

public class SpaceScanTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Adds_up_sizes_from_the_bottom_and_sorts_largest_first()
    {
        using var t = new TestFolder();
        t.File(@"a\a1.bin", 100);
        t.File(@"a\sub\s.bin", 50);
        t.File(@"b.bin", 30);
        t.Dir("c");

        var result = SpaceScanner.Scan(t.Root);

        Assert.Equal(180, result.Root.Bytes);
        Assert.Equal(3, result.Root.FileCount);
        Assert.Equal(new[] { "a", "c" }, result.Root.Folders.Select(f => f.Name));
        var a = result.Root.Folders[0];
        Assert.Equal(150, a.Bytes);
        Assert.Equal(100, a.OwnFileBytes);
        Assert.Equal(2, a.FileCount);
        Assert.Equal(Path.Combine(t.Root, "a", "sub"), a.Folders.Single().Path);
        Assert.Equal(0, result.UnreadableFolders);
    }

    [Fact]
    public void Lists_the_largest_files_and_totals_by_kind()
    {
        using var t = new TestFolder();
        t.File(@"v\clip.MP4", 300);
        t.File(@"backup.zip", 200);
        t.File(@"notes.txt", 10);
        t.File(@"x\thing.xyz", 5);

        var result = SpaceScanner.Scan(t.Root);

        Assert.Equal(new[] { "clip.MP4", "backup.zip", "notes.txt", "thing.xyz" }, result.LargestFiles.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(new[] { (FileKind.Videos, 300L), (FileKind.Archives, 200L), (FileKind.Documents, 10L), (FileKind.Other, 5L) },
            result.ByKind.Select(k => (k.Kind, k.Bytes)));
    }

    [Fact]
    public void Keeps_only_the_largest_files()
    {
        using var t = new TestFolder();
        for (int i = 1; i <= SpaceScanner.LargestCount + 5; i++) t.File($@"f\{i:D3}.bin", i);

        var result = SpaceScanner.Scan(t.Root);

        Assert.Equal(SpaceScanner.LargestCount, result.LargestFiles.Count);
        Assert.Equal(SpaceScanner.LargestCount + 5, result.LargestFiles[0].Bytes);
        Assert.Equal(6, result.LargestFiles[^1].Bytes); // 1..5 bytes did not make it
    }

    [Fact]
    public void Does_not_follow_links()
    {
        using var t = new TestFolder();
        string inside = t.Dir("inside");
        string outside = t.Dir("outside-target");
        t.File(@"outside-target\big.bin", 1000);
        if (!TestFolder.TryJunction(Path.Combine(inside, "link"), outside)) return;

        var result = SpaceScanner.Scan(inside);

        var link = result.Root.Folders.Single();
        Assert.True(link.IsLink);
        Assert.Equal(0, result.Root.Bytes);
    }

    [Fact]
    public void Finds_a_folder_and_takes_away_what_was_moved()
    {
        using var t = new TestFolder();
        t.File(@"a\a1.bin", 100);
        t.File(@"a\sub\s.bin", 50);
        t.File(@"b.bin", 30);
        var result = SpaceScanner.Scan(t.Root);

        var sub = result.Find(Path.Combine(t.Root, "a", "sub"))!;
        Assert.Equal(50, sub.Bytes);
        Assert.Null(result.Find(Path.Combine(t.Root, "nope")));
        Assert.Same(result.Root, result.Find(t.Root + @"\"));

        sub.Subtract(50, 1);
        Assert.Equal(0, sub.Bytes);
        Assert.Equal(100, result.Find(Path.Combine(t.Root, "a"))!.Bytes);
        Assert.Equal(130, result.Root.Bytes);
        Assert.Equal(2, result.Root.FileCount);
    }

    [Fact]
    public void Lists_the_files_directly_in_a_folder()
    {
        using var t = new TestFolder();
        t.File(@"small.bin", 1);
        t.File(@"large.bin", 9);
        t.File(@"sub\deep.bin", 50);

        Assert.Equal(new[] { "large.bin", "small.bin" }, SpaceScanner.FilesIn(t.Root).Select(f => Path.GetFileName(f.Path)));
        Assert.Empty(SpaceScanner.FilesIn(Path.Combine(t.Root, "missing")));
    }

    [Fact]
    public void Kinds_by_extension()
    {
        Assert.Equal(FileKind.Videos, SpaceScanner.KindOf("A.MKV"));
        Assert.Equal(FileKind.Archives, SpaceScanner.KindOf(@"C:\x\logs.tar.gz"));
        Assert.Equal(FileKind.DiskImages, SpaceScanner.KindOf("win11.iso"));
        Assert.Equal(FileKind.Other, SpaceScanner.KindOf("README"));
    }

    // ---- moving from the Space page ----

    private static SpaceMoves Rules(TestFolder t) =>
        new(new[] { Path.Combine(t.Root, "Windows") }, new[] { Path.Combine(t.Root, "Documents") });

    [Fact]
    public void Offers_only_what_may_be_moved()
    {
        using var t = new TestFolder();
        var rules = Rules(t);
        string win = t.File(@"Windows\system.dll", 1);
        string doc = t.File(@"Documents\report.docx", 1);
        string mine = t.File(@"Videos\holiday.mp4", 1);
        string locked = t.File(@"keep\a.bin", 1);
        string hidden = t.File(@"pagefile.sys", 1);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var never = new[] { Path.Combine(t.Root, "keep") };

        Assert.Equal(SpaceMoveCheck.SystemOrAppData, rules.Check(win, never));
        Assert.Equal(SpaceMoveCheck.SystemOrAppData, rules.Check(t.Root, never));            // holds Windows
        Assert.Equal(SpaceMoveCheck.PersonalFolder, rules.Check(Path.Combine(t.Root, "Documents"), never));
        Assert.Equal(SpaceMoveCheck.Ok, rules.Check(doc, never));                            // what is inside may go
        Assert.Equal(SpaceMoveCheck.Ok, rules.Check(mine, never));
        Assert.Equal(SpaceMoveCheck.NeverClean, rules.Check(locked, never));
        Assert.Equal(SpaceMoveCheck.HiddenSystem, rules.Check(hidden, never));
        Assert.Equal(SpaceMoveCheck.NotHere, rules.Check(Path.GetPathRoot(t.Root)!, never)); // a whole drive
        Assert.Equal(SpaceMoveCheck.NotHere, rules.Check(mine, never, _ => false));         // USB stick, network drive
        File.SetAttributes(hidden, FileAttributes.Normal);
    }

    [Fact]
    public void Offers_no_links()
    {
        using var t = new TestFolder();
        string target = t.Dir("target");
        string link = Path.Combine(t.Dir("here"), "link");
        if (!TestFolder.TryJunction(link, target)) return;
        Assert.Equal(SpaceMoveCheck.Link, Rules(t).Check(link, null));
    }

    [Fact]
    public void The_check_right_before_moving_applies_the_same_rules()
    {
        using var t = new TestFolder();
        var rules = Rules(t);
        string win = t.File(@"Windows\system.dll", 1);
        string docs = t.Dir("Documents");
        string mine = t.File(@"Videos\holiday.mp4", 1);
        string hidden = t.File(@"swap\swapfile.sys", 1);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        SafetyVerdict Verdict(string path) => SafetyCheck.Check(new CleanupItem(path, Directory.Exists(path), 1, Now),
            rules.CategoryFor(path), Now, _ => false, _ => true);

        Assert.Equal(SafetyVerdict.Protected, Verdict(win));
        Assert.Equal(SafetyVerdict.Protected, Verdict(docs));
        Assert.Equal(SafetyVerdict.Protected, Verdict(hidden));
        Assert.Equal(SafetyVerdict.Ok, Verdict(mine));
        File.SetAttributes(hidden, FileAttributes.Normal);
    }
}
