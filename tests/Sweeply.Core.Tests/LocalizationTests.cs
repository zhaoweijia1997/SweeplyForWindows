using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sweeply.Core.Tests;

/// <summary>Every language file must have exactly the English keys, with the same {0} placeholders.</summary>
public class LocalizationTests
{
    private static readonly string[] Languages = { "en", "zh-Hans", "zh-Hant", "ja", "ru", "es", "hi" };

    private static string LocalizationFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SweeplyForWindows.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SweeplyForWindows", "Localization");
    }

    private static Dictionary<string, string> Load(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(LocalizationFolder(), code + ".json")))!;

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal));

    public static IEnumerable<object[]> Translations() => Languages.Skip(1).Select(l => new object[] { l });

    [Theory]
    [MemberData(nameof(Translations))]
    public void Has_the_same_keys_and_placeholders_as_English(string code)
    {
        var en = Load("en");
        var tr = Load(code);

        Assert.Empty(en.Keys.Except(tr.Keys));   // missing translations
        Assert.Empty(tr.Keys.Except(en.Keys));   // leftovers
        foreach (var (key, english) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(tr[key]), $"{code}: {key} is empty");
            Assert.Equal(Placeholders(english), Placeholders(tr[key]));
        }
    }

    [Fact]
    public void Every_category_has_a_name_and_description()
    {
        var en = Load("en");
        var paths = new KnownPaths(@"C:\t", @"C:\l", @"C:\u", @"C:\d");
        foreach (var c in CategoryCatalog.Create(paths).Concat(FolderJunk.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Array.Empty<string>(), out _)))
        {
            Assert.Contains($"cat.{c.Id}.name", en.Keys);
            Assert.Contains($"cat.{c.Id}.desc", en.Keys);
        }
        foreach (var g in Enum.GetNames<CategoryGroup>())
            Assert.Contains($"clean.group.{g}", en.Keys);
    }
}
