using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, bool scanOnOpen = true)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.OwnerHandle = () => new WindowInteropHelper(this).Handle;
        viewModel.PickSaveFile = suggested =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggested, DefaultExt = ".txt", Filter = "Text (*.txt)|*.txt" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        viewModel.PickFolder = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };

        // Capture page: file dialogs, the selected line of the details, and capture files dropped on the page.
        string captureFilter = Localization.Loc.Instance["cap.fileFilter"] + " (*.pcapng;*.pcap;*.cap)|*.pcapng;*.pcap;*.cap|*.*|*.*";
        viewModel.Capture.PickOpenFile = () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = captureFilter };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        viewModel.Capture.PickSaveFile = suggested =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggested, DefaultExt = ".pcapng", Filter = "pcapng (*.pcapng)|*.pcapng" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        PacketDetails.SelectedItemChanged += (_, e) => viewModel.Capture.SelectedNode = e.NewValue as DetailNode;
        CapturePage.DragOver += (_, e) =>
        {
            e.Effects = CaptureFileOf(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        };
        CapturePage.Drop += (_, e) =>
        {
            if (CaptureFileOf(e.Data) is string path) _ = viewModel.Capture.OpenAsync(path);
        };
        // When the list of what would be moved opens, keyboard focus goes to "Back", never to the
        // confirm button, so a stray Enter or Space cannot start a clean.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsReviewing) && viewModel.IsReviewing)
                Dispatcher.BeginInvoke(() => ReviewBackButton.Focus(), DispatcherPriority.Input);
        };
        if (scanOnOpen)
            Loaded += async (_, _) => await viewModel.ScanAsync(); // scanning only reads, never changes anything
    }

    /// <summary>The one capture file being dragged, or null.</summary>
    private static string? CaptureFileOf(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && System.IO.Path.GetExtension(files[0]).ToLowerInvariant() is ".pcap" or ".pcapng" or ".cap" ? files[0] : null;
}
