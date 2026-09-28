using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SweeplyForWindows.Localization;

/// <summary>
/// Translated strings, switchable at run time. XAML binds to the indexer:
/// <c>{Binding [nav.clean], Source={x:Static loc:Loc.Instance}}</c>; changing the language
/// raises PropertyChanged("Item[]") so every binding refreshes without a restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    /// <summary>Supported languages with their names in their own language.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("en", "English"),
        new LanguageOption("zh-Hans", "简体中文"),
        new LanguageOption("zh-Hant", "繁體中文"),
        new LanguageOption("ja", "日本語"),
        new LanguageOption("ru", "Русский"),
        new LanguageOption("es", "Español"),
        new LanguageOption("hi", "हिन्दी"),
    };

    private readonly Dictionary<string, string> _english = Load("en");
    private Dictionary<string, string> _current;

    private Loc()
    {
        _current = _english;
        Language = "en";
        Culture = CultureInfo.GetCultureInfo("en");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, for view models that build text in code.</summary>
    public event Action? LanguageChanged;

    public string Language { get; private set; }

    public CultureInfo Culture { get; private set; }

    public string this[string key] =>
        _current.TryGetValue(key, out string? v) ? v :
        _english.TryGetValue(key, out string? e) ? e : key;

    public string Format(string key, params object[] args) => string.Format(Culture, this[key], args);

    public void SetLanguage(string code)
    {
        if (Languages.All(l => l.Code != code)) code = "en";
        _current = code == "en" ? _english : Load(code);
        Language = code;
        Culture = CultureInfo.GetCultureInfo(code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    /// <summary>The supported language closest to the Windows display language.</summary>
    public static string FromSystem()
    {
        var ui = CultureInfo.CurrentUICulture;
        if (ui.TwoLetterISOLanguageName == "zh")
        {
            string name = ui.Name;
            bool traditional = name.Contains("Hant") || name.EndsWith("-TW") || name.EndsWith("-HK") || name.EndsWith("-MO");
            return traditional ? "zh-Hant" : "zh-Hans";
        }
        string two = ui.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    public static Dictionary<string, string> Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        using Stream? s = asm.GetManifestResourceStream($"SweeplyForWindows.Localization.{code}.json");
        if (s is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>();
    }
}

public sealed record LanguageOption(string Code, string NativeName);
