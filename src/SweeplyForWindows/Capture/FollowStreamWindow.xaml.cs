using System.Windows;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows.Capture;

public partial class FollowStreamWindow : Window
{
    public FollowStreamWindow(FollowStreamViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickSaveFile = suggested =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggested, Filter = "*.*|*.*" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
    }
}
