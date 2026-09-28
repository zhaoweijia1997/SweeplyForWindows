namespace Sweeply.Core.Tests;

/// <summary>
/// Uses the real Windows Recycle Bin, so it only runs when SWEEPLY_RECYCLE_TEST=1
/// (otherwise every test run would leave files in the user's Recycle Bin).
/// </summary>
public class RecycleBinIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("SWEEPLY_RECYCLE_TEST") == "1";

    [Fact]
    public void Moves_a_file_and_a_folder_to_the_real_Recycle_Bin()
    {
        if (!Enabled) return;
        using var t = new TestFolder();
        string file = t.File("SweeplyForWindows-recycle-test.txt", 16);
        string folder = t.Dir("SweeplyForWindows-recycle-test-folder");
        t.File(@"SweeplyForWindows-recycle-test-folder\inside.txt", 16);
        var bin = new ShellRecycleBin();

        Assert.True(bin.TryRecycle(file, out string? e1), e1);
        Assert.True(bin.TryRecycle(folder, out string? e2), e2);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Reports_failure_for_a_missing_path()
    {
        if (!Enabled) return;
        using var t = new TestFolder();
        var bin = new ShellRecycleBin();
        Assert.False(bin.TryRecycle(Path.Combine(t.Root, "does-not-exist.txt"), out string? error));
        Assert.NotNull(error);
    }
}
