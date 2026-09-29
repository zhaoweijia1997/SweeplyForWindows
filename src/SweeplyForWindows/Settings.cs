using System.IO;
using System.Text.Json;

namespace SweeplyForWindows;

/// <summary>
/// The few things the app remembers between runs, in %LOCALAPPDATA%\SweeplyForWindows\settings.json.
/// Written by the app only; there is nothing here for people to edit.
/// </summary>
public sealed class Settings
{
    public string? Language { get; set; }

    /// <summary>Closing the window leaves the app running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The "still running" notification has been shown once already.</summary>
    public bool TrayHintShown { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweeplyForWindows", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
