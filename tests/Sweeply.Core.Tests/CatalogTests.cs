namespace Sweeply.Core.Tests;

public class CatalogTests
{
    private static readonly KnownPaths Paths = new(
        Temp: @"C:\Users\u\AppData\Local\Temp",
        LocalAppData: @"C:\Users\u\AppData\Local",
        UserProfile: @"C:\Users\u",
        Downloads: @"D:\Downloads");

    [Fact]
    public void Ids_are_unique()
    {
        var ids = CategoryCatalog.Create(Paths).Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_root_is_inside_the_user_folders()
    {
        foreach (var c in CategoryCatalog.Create(Paths))
        foreach (string root in c.Roots)
        {
            Assert.True(Path.IsPathFullyQualified(root), root);
            Assert.True(
                root.StartsWith(Paths.LocalAppData + @"\", StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(Paths.UserProfile + @"\", StringComparison.OrdinalIgnoreCase) ||
                root.Equals(Paths.Temp, StringComparison.OrdinalIgnoreCase) ||
                root.Equals(Paths.Downloads, StringComparison.OrdinalIgnoreCase),
                $"{c.Id}: {root}");
        }
    }

    [Fact]
    public void Downloads_installers_are_never_preselected()
    {
        var c = CategoryCatalog.Create(Paths).Single(x => x.Id == "download-installers");
        Assert.False(c.SelectedByDefault);
        Assert.Equal(ItemKind.Files, c.Kind);
    }

    [Fact]
    public void Temp_files_need_a_day_of_rest()
    {
        var c = CategoryCatalog.Create(Paths).Single(x => x.Id == "temp-files");
        Assert.Equal(TimeSpan.FromDays(1), c.MinimumAge);
    }

    [Fact]
    public void System_paths_resolve()
    {
        var p = KnownPaths.FromSystem();
        Assert.True(Directory.Exists(p.Temp));
        Assert.True(Path.IsPathFullyQualified(p.Downloads));
    }
}
