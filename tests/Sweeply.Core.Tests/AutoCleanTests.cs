namespace Sweeply.Core.Tests;

public class AutoCleanTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Func<string, bool> NothingRuns = _ => false;
    private static readonly Func<string, bool> LocalDisk = _ => true;
    private static readonly Func<string, long, bool> Fits = (_, _) => true;

    private static CleanupCategory Cat(string id, string root, CategoryGroup group = CategoryGroup.Browsers,
        bool selected = true, string[]? blocking = null) => new()
    {
        Id = id, Group = group, Roots = new[] { root }, SelectedByDefault = selected,
        BlockingProcesses = blocking ?? Array.Empty<string>(),
    };

    [Fact]
    public void Cleans_what_is_ticked_by_default_except_package_and_shader_caches()
    {
        var catalog = CategoryCatalog.Create(new KnownPaths(@"C:\t", @"C:\l", @"C:\u", @"C:\d"));
        var included = catalog.Where(AutoClean.IsIncluded).Select(c => c.Id).ToHashSet();

        foreach (string id in new[] { "temp-files", "crash-dumps", "error-reports", "edge-cache", "chrome-cache",
                     "wechat-cache", "wechat-logs", "wecom-cache", "qq-cache", "vscode-cache", "steam-cache" })
            Assert.Contains(id, included);
        foreach (string id in AutoClean.NotDaily)
            Assert.DoesNotContain(id, included);
        foreach (string id in new[] { "wechat-media", "wechat-files", "wecom-media", "wecom-files", "download-installers" })
            Assert.DoesNotContain(id, included); // never ticked by default, never automatic
        // A name typed wrong here would silently clean that cache every day.
        Assert.All(AutoClean.NotDaily, id => Assert.Contains(catalog, c => c.Id == id));
    }

    [Fact]
    public void Never_touches_folder_right_click_categories()
    {
        var found = FolderJunk.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Array.Empty<string>(), out _);
        Assert.DoesNotContain(found, AutoClean.IsIncluded);
    }

    [Fact]
    public void Is_due_once_a_day()
    {
        Assert.False(AutoClean.IsDue(false, null, Now));
        Assert.True(AutoClean.IsDue(true, null, Now));                  // just turned on
        Assert.False(AutoClean.IsDue(true, Now.AddHours(-23), Now));
        Assert.True(AutoClean.IsDue(true, Now.AddHours(-24), Now));
        Assert.True(AutoClean.IsDue(true, Now.AddDays(3), Now));        // clock set back
    }

    [Fact]
    public void Moves_only_the_included_categories()
    {
        using var t = new TestFolder();
        var cache = Cat("chrome-cache", t.Dir("cache"));
        var npm = Cat("npm-cache", t.Dir("npm"), CategoryGroup.Developer);
        var media = Cat("wechat-media", t.Dir("media"), CategoryGroup.Chat, selected: false);
        string a = t.File(@"cache\a.bin", 100);
        string b = t.File(@"cache\b.bin", 50);
        string pkg = t.File(@"npm\pkg.tgz", 100);
        string video = t.File(@"media\2025-01.mp4", 100);
        var bin = new FakeRecycleBin();

        var outcome = AutoClean.Run(new[] { cache, npm, media }, bin, Now, null, NothingRuns, LocalDisk, Fits);

        Assert.Equal(new[] { a, b }, bin.Recycled);
        Assert.Equal(150, outcome.MovedBytes);
        Assert.True(File.Exists(pkg));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public void Leaves_never_clean_items_and_running_apps_alone()
    {
        using var t = new TestFolder();
        var cache = Cat("chrome-cache", t.Dir("cache"));
        var edge = Cat("edge-cache", t.Dir("edge"), blocking: new[] { "msedge" });
        string keep = t.File(@"cache\keep.bin", 10);
        string go = t.File(@"cache\go.bin", 10);
        string open = t.File(@"edge\data.bin", 10);
        var bin = new FakeRecycleBin();

        AutoClean.Run(new[] { cache, edge }, bin, Now, new[] { keep }, n => n == "msedge", LocalDisk, Fits);

        Assert.Equal(new[] { go }, bin.Recycled);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(open));
    }

    [Fact]
    public void Leaves_items_the_Recycle_Bin_cannot_take()
    {
        using var t = new TestFolder();
        var cache = Cat("chrome-cache", t.Dir("cache"));
        string small = t.File(@"cache\small.bin", 100);
        string big = t.File(@"cache\big.bin", 200);
        var bin = new FakeRecycleBin();

        var outcome = AutoClean.Run(new[] { cache }, bin, Now, null, NothingRuns, LocalDisk, (_, bytes) => bytes < 150);

        Assert.Equal(new[] { small }, bin.Recycled);
        Assert.True(File.Exists(big));
        Assert.Contains(outcome.Skipped, s => s.Item.Path == big && s.Error is not null);
    }

    [Fact]
    public void Recycle_Bin_capacity_rules()
    {
        Assert.True(RecycleBinCapacity.Fits(100, nukeOnDelete: false, maxCapacityMb: null));   // never set: Windows' default
        Assert.False(RecycleBinCapacity.Fits(100, nukeOnDelete: true, maxCapacityMb: null));   // "remove files immediately"
        Assert.True(RecycleBinCapacity.Fits(1L << 20, nukeOnDelete: false, maxCapacityMb: 1));
        Assert.False(RecycleBinCapacity.Fits((1L << 20) + 1, nukeOnDelete: false, maxCapacityMb: 1));
    }
}
