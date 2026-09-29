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
}
