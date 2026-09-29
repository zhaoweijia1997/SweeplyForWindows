using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SweeplyForWindows.Platform;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows.Monitor;

/// <summary>Live values shown on the floating bar.</summary>
public sealed class MonitorBarViewModel : ObservableObject
{
    private string _download = "—", _upload = "—", _cpu = "—", _diskWrite = "—";
    public string Download { get => _download; set => SetField(ref _download, value); }
    public string Upload { get => _upload; set => SetField(ref _upload, value); }
    public string Cpu { get => _cpu; set => SetField(ref _cpu, value); }
    public string DiskWrite { get => _diskWrite; set => SetField(ref _diskWrite, value); }
}

public partial class MonitorBar : Window
{
    public MonitorBar(MonitorBarViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        // A tool window: no taskbar button and not in Alt+Tab.
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW));
        };
    }

    public event Action? OpenRequested;
    public event Action? HideRequested;

    /// <summary>The bar was dragged; its new Left and Top are passed along.</summary>
    public event Action<double, double>? Moved;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove(); // returns when the button is released
        Moved?.Invoke(Left, Top);
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();

    private void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
