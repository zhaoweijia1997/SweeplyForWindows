namespace Sweeply.Core.Tests;

public class ExclusionTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-10);

    private static CleanupCategory Cat(string root) => new() { Id = "c", Group = CategoryGroup.System, Roots = new[] { root } };

    [Theory]
    [InlineData(@"C:\keep", @"C:\keep", true)]            // the item itself
    [InlineData(@"C:\keep\a\b.txt", @"C:\keep", true)]    // inside an excluded folder
    [InlineData(@"C:\cache", @"C:\cache\keep", true)]     // contains an excluded folder
    [InlineData(@"C:\KEEP\X", @"c:\keep\", true)]         // case and trailing separator don't matter
    [InlineData(@"C:\keeper", @"C:\keep", false)]         // a longer name is not "inside"
    [InlineData(@"C:\ca", @"C:\cache\keep", false)]
    [InlineData(@"D:\keep", @"C:\keep", false)]
    public void Decides_what_the_list_covers(string path, string entry, bool expected)
    {
        Assert.Equal(expected, Exclusions.IsExcluded(path, new[] { entry }));
    }

    [Fact]
    public void An_empty_list_covers_nothing()
    {
        Assert.False(Exclusions.IsExcluded(@"C:\anything", Array.Empty<string>()));
        Assert.False(Exclusions.IsExcluded(@"C:\anything", null));
    }

    [Fact]
    public void Scan_leaves_out_and_counts_what_is_on_the_list()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        t.File(@"cache\a.bin", 10, Old);
        string keep = t.File(@"cache\keep.bin", 10, Old);
        string sub = t.Dir("cache", "sub");
        t.File(@"cache\sub\protected\x.dat", 10, Old); // "sub" contains something on the list

        var scan = Scanner.Scan(Cat(root), Now, _ => false, new[] { keep, Path.Combine(sub, "protected") });

        Assert.Equal(new[] { Path.Combine(root, "a.bin") }, scan.Items.Select(i => i.Path));
        Assert.Equal(2, scan.Excluded);
    }

    [Fact]
    public void Safety_check_and_cleaner_refuse_it_even_if_it_was_scanned_before_being_listed()
    {
        using var t = new TestFolder();
        string root = t.Dir("cache");
        string file = t.File(@"cache\late.bin", 10, Old);
        var category = Cat(root);
        var item = new CleanupItem(file, false, 10, Old);
        var list = new[] { file };

        Assert.Equal(SafetyVerdict.Excluded, SafetyCheck.Check(item, category, Now, _ => false, _ => true, list));

        var bin = new FakeRecycleBin();
        var outcome = Cleaner.Clean(new[] { (item, category) }, bin, Now, isRunning: _ => false, isLocalFixedDrive: _ => true, excluded: list);
        Assert.Empty(bin.Recycled);
        Assert.Equal(SafetyVerdict.Excluded, Assert.Single(outcome.Skipped).Verdict);
        Assert.True(File.Exists(file));
    }
}
