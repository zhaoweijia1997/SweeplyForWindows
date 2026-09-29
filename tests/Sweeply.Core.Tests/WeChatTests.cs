using System.Text;

namespace Sweeply.Core.Tests;

/// <summary>A made-up WeChat install (4.x and 3.x) with the folders that must never be offered next to the ones that may.</summary>
public class WeChatTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Old = Now.AddDays(-400);
    private static readonly DateTime Recent = Now.AddDays(-10);

    private readonly TestFolder _t = new();
    private readonly KnownPaths _paths;
    private readonly string _acct;   // 4.x account in Documents
    private readonly string _moved;  // 4.x account in a folder chosen in WeChat's settings
    private readonly string _wxid;   // 3.x account

    public WeChatTests()
    {
        _paths = new KnownPaths(_t.Dir("Temp"), _t.Dir("Local"), _t.Root, _t.Dir("Downloads"),
            RoamingAppData: _t.Dir("Roaming"), Documents: _t.Dir("Documents"));

        // ---- 4.x ----
        _acct = _t.Dir(@"Documents\xwechat_files\alex_1234");
        _t.File(@"Documents\xwechat_files\alex_1234\db_storage\message\message_0.db", 50, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\cache\2025-01\Message\h1\Thumb\a", 10, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\temp\t.tmp", 10, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\msg\attach\h1\2025-01\Img\a.dat", 10, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\msg\attach\h1\2026-09\Img\b.dat", 10, Recent);
        _t.File(@"Documents\xwechat_files\alex_1234\msg\video\2025-01\v.mp4", 10, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\msg\file\2025-01\old.zip", 10, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\msg\file\2025-01\copied-in.zip", 10);
        File.SetLastWriteTimeUtc(Path.Combine(_acct, @"msg\file\2025-01\copied-in.zip"), Old); // old date, but arrived just now
        _t.File(@"Documents\xwechat_files\alex_1234\business\emoticon\Persist\00\e", 10, Old);
        _t.File(@"Documents\xwechat_files\alex_1234\config\x", 10, Old);
        _t.File(@"Documents\xwechat_files\Backup\b.db", 10, Old);
        _t.File(@"Documents\xwechat_files\all_users\login\x", 10, Old);
        _t.File(@"Documents\xwechat_files\no_db_here\cache\2025-01\x", 10, Old); // not an account
        string xw = @"Roaming\Tencent\xwechat";
        _t.File($@"{xw}\log\mm_20250101.xlog", 10, Old);
        _t.File($@"{xw}\log\mm.mmap3", 10, Old);
        _t.File($@"{xw}\log\radium\host_20250101.xlog", 10, Old);
        _t.File($@"{xw}\radium\web\profiles\multitab_h\Cache\Cache_Data\f", 10, Old);
        _t.File($@"{xw}\radium\web\profiles\multitab_h\Local Storage\leveldb\s", 10, Old);
        _t.File($@"{xw}\radium\users\id\applet\data\d", 10, Old);
        _t.File($@"{xw}\XPlugin\Plugins\x.dll", 10, Old);
        File.WriteAllText(Path.Combine(_t.Dir($@"{xw}\config"), "51a1.ini"), "MyDocument:");

        // Moved to a folder with a Chinese name; WeChat may write that path as GBK.
        string movedParent = _t.Dir("微信数据");
        _moved = _t.Dir(@"微信数据\xwechat_files\alex_5678");
        _t.File(@"微信数据\xwechat_files\alex_5678\db_storage\message\message_0.db", 50, Old);
        _t.File(@"微信数据\xwechat_files\alex_5678\cache\2025-02\c", 10, Old);
        File.WriteAllBytes(Path.Combine(_t.Root, xw, "config", "98ae.ini"),
            CodePagesEncodingProvider.Instance.GetEncoding(936)!.GetBytes(movedParent));

        // ---- 3.x ----
        _wxid = _t.Dir(@"Documents\WeChat Files\wxid_abc");
        string fs = @"Documents\WeChat Files\wxid_abc\FileStorage";
        _t.File(@"Documents\WeChat Files\wxid_abc\Msg\MSG0.db", 50, Old);
        _t.File($@"{fs}\Cache\2025-01\c", 10, Old);
        _t.File($@"{fs}\Image\2025-01\i.dat", 10, Old);
        _t.File($@"{fs}\Video\2025-01\v.mp4", 10, Old);
        _t.File($@"{fs}\File\2025-01\f.pdf", 10, Old);
        _t.File($@"{fs}\MsgAttach\h\Image\2025-01\i.dat", 10, Old);
        _t.File($@"{fs}\MsgAttach\h\Thumb\2025-01\t.dat", 10, Old);
        _t.File($@"{fs}\MsgAttach\h\File\2025-01\g.docx", 10, Old);
        _t.File($@"{fs}\Fav\x", 10, Old);
        _t.File($@"{fs}\CustomEmotion\x", 10, Old);
        _t.File(@"Documents\WeChat Files\All Users\config\x", 10, Old);
        _t.File(@"Roaming\Tencent\WeChat\log\a.xlog", 10, Old);
        File.WriteAllText(Path.Combine(_t.Dir(@"Roaming\Tencent\WeChat\All Users\config"), "3ebffe94.ini"), "MyDocument:");

        // Folders get "now" as they are made; make every folder as old as what it holds.
        foreach (var d in new DirectoryInfo(_t.Root).EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            DateTime newest = d.EnumerateFiles("*", SearchOption.AllDirectories).Select(FileTreeTime).DefaultIfEmpty(Old).Max();
            TestFolder.Age(d.FullName, newest);
        }
    }

    public void Dispose() => _t.Dispose();

    private static DateTime FileTreeTime(FileInfo f) => f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;

    private List<string> Found(string id, Func<string, bool>? isRunning = null)
    {
        var category = CategoryCatalog.Create(_paths).Single(c => c.Id == id);
        return Scanner.Scan(category, Now, isRunning ?? (_ => false)).Items
            .Select(i => Path.GetRelativePath(_t.Root, i.Path)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Cache_covers_both_versions_the_moved_folder_and_the_web_caches()
    {
        Assert.Equal(new[]
        {
            @"Documents\WeChat Files\wxid_abc\FileStorage\Cache\2025-01",
            @"Documents\xwechat_files\alex_1234\cache\2025-01",
            @"Documents\xwechat_files\alex_1234\temp\t.tmp",
            @"Roaming\Tencent\xwechat\radium\web\profiles\multitab_h\Cache\Cache_Data",
            @"微信数据\xwechat_files\alex_5678\cache\2025-02",
        }.OrderBy(x => x, StringComparer.Ordinal), Found("wechat-cache"));
    }

    [Fact]
    public void Logs_are_only_xlog_files()
    {
        Assert.Equal(new[]
        {
            @"Roaming\Tencent\WeChat\log\a.xlog",
            @"Roaming\Tencent\xwechat\log\mm_20250101.xlog",
            @"Roaming\Tencent\xwechat\log\radium\host_20250101.xlog",
        }.OrderBy(x => x, StringComparer.Ordinal), Found("wechat-logs"));
    }

    [Fact]
    public void Pictures_and_videos_are_old_month_folders_only()
    {
        Assert.Equal(new[]
        {
            @"Documents\WeChat Files\wxid_abc\FileStorage\Image\2025-01",
            @"Documents\WeChat Files\wxid_abc\FileStorage\MsgAttach\h\Image\2025-01",
            @"Documents\WeChat Files\wxid_abc\FileStorage\MsgAttach\h\Thumb\2025-01",
            @"Documents\WeChat Files\wxid_abc\FileStorage\Video\2025-01",
            @"Documents\xwechat_files\alex_1234\msg\attach\h1\2025-01",
            @"Documents\xwechat_files\alex_1234\msg\video\2025-01",
        }.OrderBy(x => x, StringComparer.Ordinal), Found("wechat-media"));
    }

    [Fact]
    public void Received_files_are_offered_one_by_one_and_only_when_they_arrived_long_ago()
    {
        Assert.Equal(new[]
        {
            @"Documents\WeChat Files\wxid_abc\FileStorage\File\2025-01\f.pdf",
            @"Documents\WeChat Files\wxid_abc\FileStorage\MsgAttach\h\File\2025-01\g.docx",
            @"Documents\xwechat_files\alex_1234\msg\file\2025-01\old.zip",
        }.OrderBy(x => x, StringComparer.Ordinal), Found("wechat-files"));
    }

    [Fact]
    public void Chat_history_backups_and_the_app_itself_are_never_offered()
    {
        string[] never = { @"\db_storage", @"\wxid_abc\Msg\", @"\Backup", @"\all_users", @"\All Users", @"\XPlugin", @"\radium\users",
                           @"\Local Storage", @"\business", @"\config", @"\Fav", @"\CustomEmotion", @"\no_db_here", ".mmap" };
        var everything = new[] { "wechat-cache", "wechat-logs", "wechat-media", "wechat-files" }.SelectMany(id => Found(id)).ToList();
        Assert.NotEmpty(everything);
        foreach (string path in everything)
            foreach (string bad in never)
                Assert.DoesNotContain(bad, path, StringComparison.OrdinalIgnoreCase);

        // Even if asked directly, the check before moving refuses the chat database.
        var cache = CategoryCatalog.Create(_paths).Single(c => c.Id == "wechat-cache");
        var db = new CleanupItem(Path.Combine(_acct, "db_storage"), true, 50, Old);
        Assert.Equal(SafetyVerdict.OutsideCategory, SafetyCheck.Check(db, cache, Now, _ => false, _ => true));
    }

    [Fact]
    public void Nothing_is_scanned_while_WeChat_runs()
    {
        foreach (string process in new[] { "Weixin", "WeChat" })
        foreach (var c in CategoryCatalog.Create(_paths).Where(c => c.Group == CategoryGroup.Chat))
        {
            var scan = Scanner.Scan(c, Now, name => name == process);
            Assert.Equal(ScanStatus.BlockedByRunningApp, scan.Status);
            Assert.Empty(scan.Items);
        }
    }

    [Fact]
    public void Pictures_videos_and_files_are_not_preselected()
    {
        var catalog = CategoryCatalog.Create(_paths);
        Assert.True(catalog.Single(c => c.Id == "wechat-cache").SelectedByDefault);
        Assert.True(catalog.Single(c => c.Id == "wechat-logs").SelectedByDefault);
        Assert.False(catalog.Single(c => c.Id == "wechat-media").SelectedByDefault);
        Assert.False(catalog.Single(c => c.Id == "wechat-files").SelectedByDefault);
    }
}
