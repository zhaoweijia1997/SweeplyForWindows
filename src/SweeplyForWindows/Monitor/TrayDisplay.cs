using SweeplyForWindows.Localization;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows.Monitor;

/// <summary>What the notification-area icon shows.</summary>
public enum TrayDisplay
{
    AppIcon,
    Cpu,
    Download,
    Upload,
    DiskWrite,
}

/// <summary>A choice of "Remind me to clean up" in the Settings page list, named in the current language.</summary>
public sealed class ReminderOption : ObservableObject
{
    public ReminderOption(Sweeply.Core.ReminderInterval value) => Value = value;
    public Sweeply.Core.ReminderInterval Value { get; }
    public string Name => Loc.Instance[$"reminder.{Value}"];
    public void Relocalize() => OnPropertyChanged(nameof(Name));
}

/// <summary>One choice in the Settings page list, named in the current language.</summary>
public sealed class TrayDisplayOption : ObservableObject
{
    public TrayDisplayOption(TrayDisplay value) => Value = value;
    public TrayDisplay Value { get; }
    public string Name => Loc.Instance[$"tray.display.{Value}"];
    public void Relocalize() => OnPropertyChanged(nameof(Name));
}
