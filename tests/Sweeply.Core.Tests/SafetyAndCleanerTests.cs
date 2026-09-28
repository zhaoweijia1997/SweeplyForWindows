namespace Sweeply.Core.Tests;

public class SafetyAndCleanerTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-10);
    private static readonly Func<string, bool> NothingRuns = _ => false;
    private static readonly Func<string, bool> LocalDisk = _ => true;

    private static CleanupCategory Cat(string root, TimeSpan? minAge = null, string[]? blocking = null) => new()
    {
        Id = "c", Group = CategoryGroup.System, Roots = new[] { root },
        MinimumAge = minAge ?? TimeSpan.Zero,
        BlockingProcesses = blocking ?? Array.Empty<string>(),
    };

    private static CleanupItem Item(string path) => new(path, System.IO.Directory.Exists(path), 1, Old);

    private static SafetyVerdict Check(string path, CleanupCategory c, Func<string, bool>? running = null) =>
        SafetyCheck.Check(Item(path), c, Now, running ?? NothingRuns, LocalDisk);

    [Fact]
    public void Accepts_a_direct_child_of_the_category_folder()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        string file = t.File(@"cache\a.bin", 1);
        Assert.Equal(SafetyVerdict.Ok, Check(file, Cat(root)));
    }

    [Fact]
    public void Refuses_anything_outside_the_category()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        string outside = t.File(@"other\a.bin", 1);
        string nested = t.File(@"cache\sub\deep.bin", 1);
        string escape = Path.Combine(root, "..", "other", "a.bin");

        Assert.Equal(SafetyVerdict.OutsideCategory, Check(outside, Cat(root)));
        Assert.Equal(SafetyVerdict.OutsideCategory, Check(nested, Cat(root)));   // only direct children
        Assert.Equal(SafetyVerdict.OutsideCategory, Check(escape, Cat(root)));   // ".." can't sneak out
    }

    [Fact]
    public void Refuses_the_category_folder_itself()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        Assert.Equal(SafetyVerdict.IsCategoryRoot, Check(root, Cat(root)));
        Assert.Equal(SafetyVerdict.IsCategoryRoot, Check(root + @"\", Cat(root)));
    }

    [Fact]
    public void Reports_items_that_are_already_gone()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        Assert.Equal(SafetyVerdict.Missing, Check(Path.Combine(root, "gone.bin"), Cat(root)));
    }

    [Fact]
    public void Refuses_while_the_owning_app_runs()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        string file = t.File(@"cache\a.bin", 1);
        Assert.Equal(SafetyVerdict.AppRunning, Check(file, Cat(root, blocking: new[] { "chrome" }), n => n == "chrome"));
    }

    [Fact]
    public void Refuses_temp_items_that_became_recent_since_the_scan()
    {
        using var t = new TestFolder();
        string root = t.Dir("temp");
        string dir = t.Dir("temp", "job");
        t.File(@"temp\job\new.log", 1, Now.AddMinutes(-2));
        TestFolder.Age(dir, Old);
        Assert.Equal(SafetyVerdict.TooRecent, Check(dir, Cat(root, TimeSpan.FromDays(1))));
    }

    [Fact]
    public void Refuses_a_junction_item()
    {
        using var t = new TestFolder();
        string root = t.Dir("temp");
        string target = t.Dir("precious");
        string link = Path.Combine(root, "link");
        if (!TestFolder.TryJunction(link, target)) return;
        Assert.Equal(SafetyVerdict.ReparsePoint, Check(link, Cat(root)));
    }

    [Fact]
    public void Refuses_drives_that_are_not_local_fixed_disks()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        string file = t.File(@"cache\a.bin", 1);
        Assert.Equal(SafetyVerdict.NotLocalFixedDrive, SafetyCheck.Check(Item(file), Cat(root), Now, NothingRuns, _ => false));
        Assert.False(SafetyCheck.IsLocalFixedDrive(@"\\server\share\x"));
    }

    [Fact]
    public void Cleaner_moves_what_is_safe_and_reports_the_rest()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        var cat = Cat(root);
        string a = t.File(@"cache\a.bin", 100);
        string b = t.File(@"cache\b.bin", 200);
        string outside = t.File(@"other\c.bin", 300);
        var bin = new FakeRecycleBin();
        var selection = new List<(CleanupItem, CleanupCategory)>
        {
            (new CleanupItem(a, false, 100, Old), cat),
            (new CleanupItem(b, false, 200, Old), cat),
            (new CleanupItem(outside, false, 300, Old), cat),
            (new CleanupItem(Path.Combine(root, "gone.bin"), false, 400, Old), cat),
        };
        var reports = new List<(int, int)>();

        var outcome = Cleaner.Clean(selection, bin, Now, new SyncProgress(reports), default, NothingRuns, LocalDisk);

        Assert.Equal(2, outcome.MovedCount);
        Assert.Equal(300, outcome.MovedBytes);
        Assert.Equal(new[] { a, b }, bin.Recycled);
        Assert.True(System.IO.File.Exists(outside));
        Assert.Equal(new[] { SafetyVerdict.OutsideCategory, SafetyVerdict.Missing }, outcome.Skipped.Select(s => s.Verdict));
        Assert.Equal((4, 4), reports.Last());
    }

    /// <summary>IProgress that records synchronously (Progress&lt;T&gt; posts to a thread-pool context).</summary>
    private sealed class SyncProgress(List<(int, int)> sink) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => sink.Add(value);
    }
}
