namespace Sweeply.Core.Tests;

/// <summary>"Scan this folder": made-up project folders, with look-alikes that must stay.</summary>
public class FolderJunkTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Old = Now.AddDays(-30);
    private readonly TestFolder _t = new();

    public void Dispose() => _t.Dispose();

    private Dictionary<string, List<string>> Scan(string folder, params string[] systemFolders)
    {
        var categories = FolderJunk.Find(folder, systemFolders, out bool truncated);
        Assert.False(truncated);
        return categories.ToDictionary(c => c.Id, c =>
            Scanner.Scan(c, Now, _ => false).Items.Select(i => Path.GetRelativePath(_t.Root, i.Path))
                .OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Build_folders_count_only_next_to_their_project_file()
    {
        _t.File(@"web\package.json", 10);
        _t.File(@"web\node_modules\lodash\package.json", 10);
        _t.File(@"web\node_modules\lodash\node_modules\x\index.js", 10);   // part of the one above
        _t.File(@"app\App.csproj", 10);
        _t.File(@"app\bin\Debug\app.dll", 10);
        _t.File(@"app\obj\project.assets.json", 10);
        _t.File(@"rust\Cargo.toml", 10);
        _t.File(@"rust\target\debug\app.exe", 10);
        _t.File(@"java\pom.xml", 10);
        _t.File(@"java\target\classes\A.class", 10);
        _t.File(@"android\build.gradle.kts", 10);
        _t.File(@"android\build\outputs\app.apk", 10);
        _t.File(@"android\.gradle\8.0\x.lock", 10);
        _t.File(@"py\pkg\__pycache__\m.cpython-313.pyc", 10);
        _t.File(@"py\.pytest_cache\v\cache\lastfailed", 10);

        // Same names, no project file: these are someone's own folders.
        _t.File(@"notes\bin\recipe.txt", 10);
        _t.File(@"notes\build\plan.docx", 10);
        _t.File(@"notes\target\goals.txt", 10);
        _t.File(@"loose\node_modules\a\package.json", 10);
        _t.File(@"loose\node_modules\a\node_modules\b\index.js", 10);

        var found = Scan(_t.Root);

        Assert.Equal(new[] { @"web\node_modules" }, found["folder-node-modules"]);
        Assert.Equal(new[] { @"app\bin", @"app\obj" }, found["folder-dotnet-build"]);
        Assert.Equal(new[] { @"java\target", @"rust\target" }, found["folder-target"]);
        Assert.Equal(new[] { @"android\.gradle", @"android\build" }, found["folder-gradle-build"]);
        Assert.Equal(new[] { @"py\.pytest_cache", @"py\pkg\__pycache__" }, found["folder-python-cache"]);
    }

    [Fact]
    public void Office_owner_files_and_temp_files_must_be_a_day_old()
    {
        _t.File(@"docs\~$report.docx", 1, Old);
        _t.File(@"docs\~$budget.xlsx", 1);                 // a document open right now
        _t.File(@"docs\report.docx", 10, Old);
        _t.File(@"docs\photos\Thumbs.db", 10, Old);
        _t.File(@"docs\photos\.DS_Store", 10, Old);
        _t.File(@"docs\photos\holiday.jpg", 10, Old);
        _t.File(@"docs\download.tmp", 10, Old);
        File.SetAttributes(Path.Combine(_t.Root, @"docs\~$report.docx"), FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(_t.Root, @"docs\photos\Thumbs.db"), FileAttributes.Hidden | FileAttributes.System);

        var found = Scan(_t.Root);

        Assert.Equal(new[] { @"docs\~$report.docx" }, found["folder-office-temp"]);
        Assert.Equal(new[] { @"docs\download.tmp", @"docs\photos\.DS_Store", @"docs\photos\Thumbs.db" }, found["folder-temp-files"]);
    }

    [Fact]
    public void Tool_folders_version_control_and_links_are_not_entered()
    {
        _t.File(@"home\.vscode\extensions\ext-1.0\package.json", 10);
        _t.File(@"home\.vscode\extensions\ext-1.0\node_modules\dep\index.js", 10);
        _t.File(@"home\repo\.git\objects\pack\p.pack", 10);
        _t.File(@"home\repo\.git\hooks\x.tmp", 10, Old);
        _t.File(@"home\AppData\Local\Temp\a.tmp", 10, Old);
        _t.File(@"home\Programs\Tool\Tool.csproj", 10);
        _t.File(@"home\Programs\Tool\bin\tool.dll", 10);
        string target = _t.Dir("elsewhere");
        _t.File(@"elsewhere\Linked.csproj", 10);
        _t.File(@"elsewhere\bin\x.dll", 10);
        bool linked = TestFolder.TryJunction(Path.Combine(_t.Root, @"home\link"), target);

        var found = Scan(Path.Combine(_t.Root, "home"), Path.Combine(_t.Root, @"home\Programs"));

        Assert.All(found.Values, list => Assert.Empty(list));
        if (linked) Assert.True(Directory.Exists(Path.Combine(_t.Root, @"home\link\bin")));
    }

    [Fact]
    public void Nothing_found_is_an_empty_list_not_a_missing_app()
    {
        _t.File(@"plain\letter.txt", 10);
        var categories = FolderJunk.Find(Path.Combine(_t.Root, "plain"), Array.Empty<string>(), out _);
        Assert.Equal(FolderJunk.CategoryIds, categories.Select(c => c.Id));
        Assert.All(categories, c => Assert.Equal(CategoryGroup.Folder, c.Group));
        Assert.All(categories, c => Assert.Empty(c.Roots));
        Assert.False(categories.Single(c => c.Id == "folder-node-modules").SelectedByDefault);
        Assert.False(categories.Single(c => c.Id == "folder-dotnet-build").SelectedByDefault);
        Assert.True(categories.Single(c => c.Id == "folder-python-cache").SelectedByDefault);
    }

    [Fact]
    public void Found_items_pass_the_check_before_moving_and_look_alikes_do_not()
    {
        _t.File(@"app\App.csproj", 10);
        _t.File(@"app\bin\a.dll", 10);
        _t.File(@"app\src\Program.cs", 10);
        var dotnet = FolderJunk.Find(_t.Root, Array.Empty<string>(), out _).Single(c => c.Id == "folder-dotnet-build");
        var item = Scanner.Scan(dotnet, Now, _ => false).Items.Single();

        Assert.Equal(SafetyVerdict.Ok, SafetyCheck.Check(item, dotnet, Now, _ => false, _ => true));
        var source = new CleanupItem(Path.Combine(_t.Root, @"app\src"), true, 10, Old);
        Assert.Equal(SafetyVerdict.OutsideCategory, SafetyCheck.Check(source, dotnet, Now, _ => false, _ => true));
    }

    [Theory]
    [InlineData(new[] { "--scan-folder", @"D:\work\app" }, @"D:\work\app")]
    [InlineData(new[] { "--scan-folder", "D:\"" }, @"D:\")]            // what Windows makes of "D:\"
    [InlineData(new[] { "--scan-folder", "D:" }, @"D:\")]
    [InlineData(new[] { "--background" }, null)]
    [InlineData(new[] { "--scan-folder" }, null)]
    [InlineData(new[] { "--scan-folder", "  " }, null)]
    public void Reads_the_folder_from_the_command_line(string[] args, string? expected) =>
        Assert.Equal(expected, FolderJunk.FolderFromArgs(args));

    [Fact]
    public void Windows_programs_and_app_data_cannot_be_scanned()
    {
        string programs = _t.Dir("Program Files");
        string inside = _t.Dir(@"Program Files\Vendor\App");
        string projects = _t.Dir("projects");
        string[] system = { programs };

        Assert.Equal(FolderCheck.SystemFolder, FolderJunk.Check(programs, system, _ => true));
        Assert.Equal(FolderCheck.SystemFolder, FolderJunk.Check(inside, system, _ => true));
        Assert.Equal(FolderCheck.Ok, FolderJunk.Check(projects, system, _ => true));
        Assert.Equal(FolderCheck.NotLocalFixedDrive, FolderJunk.Check(projects, system, _ => false));
        Assert.Equal(FolderCheck.Missing, FolderJunk.Check(Path.Combine(_t.Root, "gone"), system, _ => true));
        Assert.Contains(FolderJunk.SystemFolders(), f => f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase));
    }
}
