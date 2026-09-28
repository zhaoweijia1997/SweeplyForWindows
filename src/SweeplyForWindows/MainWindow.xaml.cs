using System.Windows;
using System.Windows.Interop;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, bool scanOnOpen = true)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Confirm = (title, message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        viewModel.OwnerHandle = () => new WindowInteropHelper(this).Handle;
        if (scanOnOpen)
            Loaded += async (_, _) => await viewModel.ScanAsync(); // scanning only reads, never changes anything
    }
}
