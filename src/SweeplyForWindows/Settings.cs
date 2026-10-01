using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SweeplyForWindows.Monitor;

namespace SweeplyForWindows;

/// <summary>
/// The few things the app remembers between runs, in %LOCALAPPDATA%\SweeplyForWindows\settings.json.
/// Written by the app only; there is nothing here for people to edit.
/// </summary>
public sealed class Settings
{
    public string? Language { get; set; }

    /// <summary>The "Never clean" list: folders and files that are never offered or moved.</summary>
    public List<string> ExcludedFolders { get; set; } = new();

    /// <summary>Closing the window leaves the app running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The "still running" notification has been shown once already.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>A live number on the notification-area icon instead of the app icon.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrayDisplay TrayDisplay { get; set; } = TrayDisplay.AppIcon;

    /// <summary>Pointing at the icon shows network, CPU and disk activity.</summary>
    public bool TrayToolTipStats { get; set; } = true;

    public bool ShowMonitorBar { get; set; }

    /// <summary>How often to look and say when enough can be cleaned.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Sweeply.Core.ReminderInterval CleanReminder { get; set; } = Sweeply.Core.ReminderInterval.Off;

    /// <summary>The last look for the reminder, or the last clean; the wait counts from here.</summary>
    public DateTime? LastReminderUtc { get; set; }

    /// <summary>Where the floating bar was last dragged to (device-independent pixels).</summary>
    public double? MonitorBarLeft { get; set; }
    public double? MonitorBarTop { get; set; }

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
