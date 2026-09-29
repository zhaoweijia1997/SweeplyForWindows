namespace Sweeply.Core.Tests;

public class ScannerTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-10);
    private static readonly Func<string, bool> NothingRuns = _ => false;

    private static CleanupCategory Temp(string root) => new()
    {
        Id = "t", Group = CategoryGroup.System, Roots = new[] { root }, MinimumAge = TimeSpan.FromDays(1),
    };

    [Fact]
    public void Offers_old_items_and_leaves_recent_ones()
    {
        using var t = new TestFolder();
        string root = t.Dir("temp");
        t.File(@"temp\old.tmp", 100, Old);
        t.File(@"temp\new.tmp", 100, Now.AddHours(-1));
        t.File(@"temp\oldDir\a.bin", 300, Old);
        TestFolder.Age(Path.Combine(root, "oldDir"), Old);
        t.File(@"temp\busyDir\a.bin", 50, Old);
        t.File(@"temp\busyDir\sub\fresh.bin", 50, Now.AddMinutes(-5)); // something inside is recent
        TestFolder.Age(Path.Combine(root, "busyDir", "sub"), Old);
        TestFolder.Age(Path.Combine(root, "busyDir"), Old);

        var scan = Scanner.Scan(Temp(root), Now, NothingRuns);

        Assert.Equal(ScanStatus.Found, scan.Status);
        Assert.Equal(new[] { "oldDir", "old.tmp" }, scan.Items.Select(i => Path.GetFileName(i.Path)));
        Assert.Equal(400, scan.TotalBytes);
        Assert.True(scan.Items[0].IsDirectory);
    }

    [Fact]
    public void Files_kind_only_takes_matching_files()
    {
        using var t = new TestFolder();
        string root = t.Dir("dumps");
        t.File(@"dumps\app.dmp", 10);
        t.File(@"dumps\APP2.DMP", 10);
        t.File(@"dumps\notes.txt", 10);
        t.Dir("dumps", "folder.dmp");
        var cat = new CleanupCategory
        {
            Id = "d", Group = CategoryGroup.System, Roots = new[] { root },
            Kind = ItemKind.Files, NamePatterns = new[] { "*.dmp" },
        };

        var scan = Scanner.Scan(cat, Now, NothingRuns);

        Assert.Equal(new[] { "APP2.DMP", "app.dmp" }, scan.Items.Select(i => Path.GetFileName(i.Path)).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Skips_the_whole_category_while_its_app_runs()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        t.File(@"cache\x.bin", 10);
        var cat = new CleanupCategory
        {
            Id = "b", Group = CategoryGroup.Browsers, Roots = new[] { root }, BlockingProcesses = new[] { "msedge" },
        };

        var scan = Scanner.Scan(cat, Now, name => name == "msedge");

        Assert.Equal(ScanStatus.BlockedByRunningApp, scan.Status);
        Assert.Equal("msedge", scan.BlockingProcess);
        Assert.Empty(scan.Items);
    }

    [Fact]
    public void Missing_folders_mean_the_app_is_not_installed()
    {
        var cat = new CleanupCategory
        {
            // A local path that doesn't exist (a drive letter like Z: may be a disconnected network share and hang).
            Id = "m", Group = CategoryGroup.Developer, Roots = new[] { Path.Combine(Path.GetTempPath(), "SweeplyTests", Guid.NewGuid().ToString("N")) },
            BlockingProcesses = new[] { "app" },
        };
        // Not "the app is running" either: there is nothing of it here to clean.
        Assert.Equal(ScanStatus.NotInstalled, Scanner.Scan(cat, Now, _ => true).Status);
        Assert.False(Scanner.IsPresent(cat));
    }

    [Fact]
    public void An_existing_but_empty_folder_means_nothing_found()
    {
        using var t = new TestFolder();
        var cat = new CleanupCategory { Id = "e", Group = CategoryGroup.Developer, Roots = new[] { t.Dir("empty") } };
        Assert.Equal(ScanStatus.NothingFound, Scanner.Scan(cat, Now, NothingRuns).Status);
    }

    [Fact]
    public void Never_offers_or_follows_a_junction()
    {
        using var t = new TestFolder();
        string root = t.Dir("temp");
        string elsewhere = t.Dir("precious");
        t.File(@"precious\keep.bin", 5000, Old);
        t.File(@"temp\old.tmp", 10, Old);
        t.File(@"temp\dir\a.bin", 10, Old);
        if (!TestFolder.TryJunction(Path.Combine(root, "link"), elsewhere)) return; // can't create junctions here
        if (!TestFolder.TryJunction(Path.Combine(root, "dir", "inner"), elsewhere)) return;
        // No minimum age here: a freshly made junction counts as recent activity, which is right
        // for temp files but not what this test is about.
        var anyAge = new CleanupCategory { Id = "t", Group = CategoryGroup.System, Roots = new[] { root } };

        var scan = Scanner.Scan(anyAge, Now, NothingRuns);

        Assert.DoesNotContain(scan.Items, i => Path.GetFileName(i.Path) == "link");
        var dir = Assert.Single(scan.Items, i => Path.GetFileName(i.Path) == "dir");
        Assert.Equal(10, dir.Bytes); // the 5000-byte file behind the inner junction is not counted
    }
}
