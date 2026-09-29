namespace Sweeply.Core.Tests;

/// <summary>Made-up browser folders: caches next to the things that must stay (cookies, storage, bookmarks).</summary>
public class BrowserTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly TestFolder _t = new();
    private readonly KnownPaths _paths;

    public BrowserTests()
    {
        _paths = new KnownPaths(_t.Dir("Temp"), _t.Dir("Local"), _t.Root, _t.Dir("Downloads"),
            RoamingAppData: _t.Dir("Roaming"), Documents: _t.Dir("Documents"));

        _t.File(@"Local\Mozilla\Firefox\Profiles\abc.default-release\cache2\entries\0A1B", 10);
        _t.File(@"Local\Mozilla\Firefox\Profiles\abc.default-release\cache2\index", 10);
        _t.File(@"Local\Mozilla\Firefox\Profiles\abc.default-release\startupCache\startupCache.8.little", 10);
        _t.File(@"Roaming\Mozilla\Firefox\Profiles\abc.default-release\places.sqlite", 10); // bookmarks and history

        _t.File(@"Local\Opera Software\Opera Stable\Cache\Cache_Data\f", 10);
        _t.File(@"Local\Opera Software\Opera GX Stable\Default\Cache\Cache_Data\f", 10);
        _t.File(@"Local\Opera Software\Opera Stable\Local Storage\leveldb\x", 10);

        _t.File(@"Roaming\360se6\User Data\Default\Cache\Cache_Data\f", 10);
        _t.File(@"Roaming\360se6\User Data\Default\Cookies", 10);
        _t.File(@"Local\360ChromeX\Chrome\User Data\Profile 1\GPUCache\data_0", 10);

        _t.File(@"Local\Google\Chrome Beta\User Data\Profile 1\Code Cache\js\x", 10);
        _t.File(@"Local\Google\Chrome Beta\User Data\Profile 1\Cookies", 10);
        _t.File(@"Local\Google\Chrome Beta\User Data\System Profile\Cache\Cache_Data\f", 10); // not a person's profile
    }

    public void Dispose() => _t.Dispose();

    private List<string> Found(string id)
    {
        var category = CategoryCatalog.Create(_paths).Single(c => c.Id == id);
        return Scanner.Scan(category, Now, _ => false).Items
            .Select(i => Path.GetRelativePath(_t.Root, i.Path)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Firefox_cache_lives_under_local_app_data()
    {
        Assert.Equal(new[]
        {
            @"Local\Mozilla\Firefox\Profiles\abc.default-release\cache2\entries",
            @"Local\Mozilla\Firefox\Profiles\abc.default-release\cache2\index",
            @"Local\Mozilla\Firefox\Profiles\abc.default-release\startupCache\startupCache.8.little",
        }, Found("firefox-cache"));
    }

    [Fact]
    public void Opera_old_and_new_layouts()
    {
        Assert.Equal(new[]
        {
            @"Local\Opera Software\Opera GX Stable\Default\Cache\Cache_Data",
            @"Local\Opera Software\Opera Stable\Cache\Cache_Data",
        }, Found("opera-cache"));
    }

    [Fact]
    public void Browser_360_covers_its_three_editions()
    {
        Assert.Equal(new[]
        {
            @"Local\360ChromeX\Chrome\User Data\Profile 1\GPUCache\data_0",
            @"Roaming\360se6\User Data\Default\Cache\Cache_Data",
        }, Found("360-cache"));
    }

    [Fact]
    public void Chrome_beta_counts_as_chrome_and_only_person_profiles()
    {
        Assert.Equal(new[] { @"Local\Google\Chrome Beta\User Data\Profile 1\Code Cache\js" }, Found("chrome-cache"));
    }

    [Fact]
    public void Browsers_that_are_not_here_are_not_installed()
    {
        foreach (string id in new[] { "brave-cache", "vivaldi-cache", "yandex-cache", "qqbrowser-cache", "edge-cache" })
        {
            var category = CategoryCatalog.Create(_paths).Single(c => c.Id == id);
            Assert.Equal(ScanStatus.NotInstalled, Scanner.Scan(category, Now, _ => true).Status);
        }
    }
}
