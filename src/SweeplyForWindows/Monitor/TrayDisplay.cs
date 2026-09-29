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

/// <summary>One choice in the Settings page list, named in the current language.</summary>
public sealed class TrayDisplayOption : ObservableObject
{
    public TrayDisplayOption(TrayDisplay value) => Value = value;
    public TrayDisplay Value { get; }
    public string Name => Loc.Instance[$"tray.display.{Value}"];
    public void Relocalize() => OnPropertyChanged(nameof(Name));
}
