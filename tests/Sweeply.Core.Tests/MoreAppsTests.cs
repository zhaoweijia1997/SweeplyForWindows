namespace Sweeply.Core.Tests;

/// <summary>Made-up folders for WeCom, VS Code, graphics caches and apps built on Chromium, with the things that must stay.</summary>
public class MoreAppsTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Old = Now.AddDays(-400);

    private readonly TestFolder _t = new();
    private readonly KnownPaths _paths;

    public MoreAppsTests()
    {
        _paths = new KnownPaths(_t.Dir("Temp"), _t.Dir("Local"), _t.Root, _t.Dir("Downloads"),
            RoamingAppData: _t.Dir("Roaming"), Documents: _t.Dir("Documents"));
    }

    public void Dispose() => _t.Dispose();

    private void ChromiumCache(string folder)
    {
        _t.File(folder + @"\Cache\Cache_Data\index", 10, Old);
        _t.File(folder + @"\Cache\Cache_Data\f_000001", 10, Old);
        _t.File(folder + @"\Code Cache\js\index", 10, Old);
        _t.File(folder + @"\GPUCache\index", 10, Old);
        _t.File(folder + @"\GPUCache\data_0", 10, Old);
    }

    private void AgeAll()
    {
        foreach (var d in new DirectoryInfo(_t.Root).EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            DateTime newest = d.EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc).DefaultIfEmpty(Old).Max();
            TestFolder.Age(d.FullName, newest);
        }
    }

    private List<string> Found(string id) =>
        Scanner.Scan(CategoryCatalog.Create(_paths).Single(c => c.Id == id), Now, _ => false).Items
            .Select(i => Path.GetRelativePath(_t.Root, i.Path)).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void Chromium_caches_are_recognised_by_what_is_inside_not_by_name()
    {
        ChromiumCache(@"Roaming\SomeApp");
        _t.File(@"Roaming\SomeApp\Partitions\p1\Cache\Cache_Data\index", 10);     // deeper profile
        _t.File(@"Roaming\SomeApp\Cache\notes.txt", 10);                           // stays: inside the cache folder, found as part of it
        _t.File(@"Roaming\Other\Cache\settings.json", 10);                         // a folder called Cache that is not one
        _t.File(@"Roaming\Other\GPUCache\readme.txt", 10);
        _t.File(@"Roaming\Other\User\Cache\Cache_Data\index", 10);                 // inside "User": never searched
        _t.File(@"Roaming\Other\WeDrive\Cache\Cache_Data\index", 10);              // synced files: never searched

        var found = ChromiumCaches.FindAll(new[] { _t.Dir(@"Roaming\SomeApp"), _t.Dir(@"Roaming\Other") })
            .Select(p => Path.GetRelativePath(_t.Root, p)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(new[]
        {
            @"Roaming\SomeApp\Cache",
            @"Roaming\SomeApp\Code Cache",
            @"Roaming\SomeApp\GPUCache",
            @"Roaming\SomeApp\Partitions\p1\Cache",
        }, found);
    }

    [Fact]
    public void WeCom_offers_caches_logs_and_old_media_never_its_data()
    {
        string a = @"Documents\WXWork\1688850000000001";
        _t.File($@"{a}\Data\message.db", 10, Old);
        _t.File($@"{a}\Index\search.db", 10, Old);
        _t.File($@"{a}\Backup\b.db", 10, Old);
        _t.File($@"{a}\Emotion\e.gif", 10, Old);
        _t.File($@"{a}\WeDrive\Project\Cache\Cache_Data\index", 10, Old);  // a synced folder that looks like a cache
        _t.File($@"{a}\WeDrive\Project\plan.docx", 10, Old);
        _t.File($@"{a}\Cache\File\2025-01\old.zip", 10, Old);
        _t.File($@"{a}\Cache\File\2026-09\new.zip", 10);
        _t.File($@"{a}\Cache\File\Temp\t1.tmp", 10, Old);
        _t.File($@"{a}\Cache\Image\2025-01\i.jpg", 10, Old);
        _t.File($@"{a}\Cache\Image\Temp\t2.tmp", 10, Old);
        _t.File($@"{a}\Cache\Video\2025-01\v.mp4", 10, Old);
        _t.File($@"{a}\Cache\Video\Temp\t3.tmp", 10, Old);
        ChromiumCache($@"{a}\WXWorkCefCache");
        _t.File(@"Documents\WXWork\qtCef\Code Cache\js\index", 10, Old);
        _t.File(@"Documents\WXWork\qtCef\Service Worker\CacheStorage\origin1\x", 10, Old);
        _t.File(@"Documents\WXWork\qtCef\IndexedDB\doc.leveldb\000001.ldb", 10, Old);
        _t.File(@"Documents\WXWork\component_crx_cache\c.crx", 10, Old);
        _t.File(@"Roaming\Tencent\WXWork\Log\a.log", 10, Old);
        _t.File(@"Roaming\Tencent\WXWork\Log\CEF\b.xlog", 10, Old);
        _t.File(@"Roaming\Tencent\WXWork\Log\keep.txt", 10, Old);
        _t.File(@"Roaming\Tencent\WXWork\upgrade\5.0\setup.exe", 10, Old);
        AgeAll();

        Assert.Equal(new[]
        {
            @"Documents\WXWork\1688850000000001\Cache\File\Temp\t1.tmp",
            @"Documents\WXWork\1688850000000001\Cache\Image\Temp\t2.tmp",
            @"Documents\WXWork\1688850000000001\Cache\Video\Temp\t3.tmp",
            @"Documents\WXWork\1688850000000001\WXWorkCefCache\Cache\Cache_Data",
            @"Documents\WXWork\1688850000000001\WXWorkCefCache\Code Cache\js",
            @"Documents\WXWork\1688850000000001\WXWorkCefCache\GPUCache\data_0",
            @"Documents\WXWork\1688850000000001\WXWorkCefCache\GPUCache\index",
            @"Documents\WXWork\component_crx_cache\c.crx",
            @"Documents\WXWork\qtCef\Code Cache\js",
            @"Documents\WXWork\qtCef\Service Worker\CacheStorage\origin1",
        }, Found("wecom-cache"));
        Assert.Equal(new[] { @"Roaming\Tencent\WXWork\Log\CEF\b.xlog", @"Roaming\Tencent\WXWork\Log\a.log" }, Found("wecom-logs"));
        Assert.Equal(new[]
        {
            @"Documents\WXWork\1688850000000001\Cache\Image\2025-01",
            @"Documents\WXWork\1688850000000001\Cache\Video\2025-01",
        }, Found("wecom-media"));
        Assert.Equal(new[] { @"Documents\WXWork\1688850000000001\Cache\File\2025-01\old.zip" }, Found("wecom-files"));

        string[] never = { @"\Data\", @"\Index\", @"\Backup\", @"\Emotion\", @"\WeDrive\", @"\IndexedDB\", @"\upgrade\", "keep.txt" };
        foreach (string id in new[] { "wecom-cache", "wecom-logs", "wecom-media", "wecom-files" })
            foreach (string path in Found(id))
                foreach (string bad in never)
                    Assert.DoesNotContain(bad, path, StringComparison.OrdinalIgnoreCase);

        var catalog = CategoryCatalog.Create(_paths);
        Assert.False(catalog.Single(c => c.Id == "wecom-media").SelectedByDefault);
        Assert.False(catalog.Single(c => c.Id == "wecom-files").SelectedByDefault);
        Assert.Equal(ScanStatus.BlockedByRunningApp,
            Scanner.Scan(catalog.Single(c => c.Id == "wecom-cache"), Now, n => n == "WXWork").Status);
    }

    [Fact]
    public void VS_Code_offers_caches_logs_and_crash_dumps_never_settings()
    {
        ChromiumCache(@"Roaming\Code");
        _t.File(@"Roaming\Code\User\settings.json", 10, Old);
        _t.File(@"Roaming\Code\WebStorage\1\data.db", 10, Old);
        _t.File(@"Roaming\Code\CachedData\abc123\x.code", 10, Old);
        _t.File(@"Roaming\Code\CachedExtensionVSIXs\some.ext-1.0.0", 10, Old);
        _t.File(@"Roaming\Code\Crashpad\reports\a.dmp", 10, Old);
        _t.File(@"Roaming\Code\Crashpad\settings.dat", 10, Old);
        _t.File(@"Roaming\Code\logs\20260101T000000\main.log", 10, Old);
        AgeAll();

        Assert.Equal(new[]
        {
            @"Roaming\Code\Cache\Cache_Data",
            @"Roaming\Code\CachedData\abc123",
            @"Roaming\Code\CachedExtensionVSIXs\some.ext-1.0.0",
            @"Roaming\Code\Code Cache\js",
            @"Roaming\Code\Crashpad\reports\a.dmp",
            @"Roaming\Code\GPUCache\data_0",
            @"Roaming\Code\GPUCache\index",
            @"Roaming\Code\logs\20260101T000000",
        }, Found("vscode-cache"));
        Assert.Equal(ScanStatus.BlockedByRunningApp,
            Scanner.Scan(CategoryCatalog.Create(_paths).Single(c => c.Id == "vscode-cache"), Now, n => n == "Code").Status);
    }

    [Fact]
    public void Graphics_caches_skip_what_was_written_today()
    {
        _t.File(@"Local\D3DSCache\app1\cache.idx", 10, Old);
        _t.File(@"AppData\LocalLow\Intel\ShaderCache\s1", 10, Old);
        _t.File(@"Local\NVIDIA\DXCache\fresh.bin", 10);   // a game may be writing it right now
        AgeAll();

        Assert.Equal(new[] { @"AppData\LocalLow\Intel\ShaderCache\s1", @"Local\D3DSCache\app1" }, Found("gpu-shader-cache"));
    }

    [Fact]
    public void Apps_built_on_Chromium_offer_only_their_caches()
    {
        ChromiumCache(@"Roaming\discord");
        _t.File(@"Roaming\discord\Local Storage\leveldb\x.ldb", 10, Old);
        _t.File(@"Roaming\discord\settings.json", 10, Old);
        AgeAll();

        Assert.Equal(new[]
        {
            @"Roaming\discord\Cache\Cache_Data",
            @"Roaming\discord\Code Cache\js",
            @"Roaming\discord\GPUCache\data_0",
            @"Roaming\discord\GPUCache\index",
        }, Found("discord-cache"));
        Assert.Equal(ScanStatus.NotInstalled,
            Scanner.Scan(CategoryCatalog.Create(_paths).Single(c => c.Id == "slack-cache"), Now, _ => false).Status);
    }
}
