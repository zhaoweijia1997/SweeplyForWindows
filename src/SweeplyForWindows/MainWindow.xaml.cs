using System.Reflection;
using System.Windows;

namespace SweeplyForWindows;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        // Strip the "+commit" suffix the SDK appends.
        VersionText.Text = "v" + version.Split('+')[0];
    }
}
