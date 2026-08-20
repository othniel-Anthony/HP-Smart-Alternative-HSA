using System.Reflection;
using System.Windows;

namespace HSA;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Show the running app version in the top app bar. About content lives in
        // the Settings tab now.
        VersionText.Text = ReadVersion();
    }

    private static string ReadVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        // .NET 8 SDK appends "+<git-sha>" to InformationalVersion when building from a
        // git repo. The SHA is noise for the UI; strip everything from "+" onwards.
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
