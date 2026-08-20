using System.Windows;

namespace HSA;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnAboutClicked(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "HSA  •  v0.1.0\n\n" +
            "Built by Circuit & Ink as a no-nonsense replacement for HP Smart / the HP App.\n\n" +
            "Manages printers, drivers, and firmware directly through the Windows print spooler, " +
            "WMI, and (for network printers) SNMP / IPP.\n\n" +
            "Logs: %LOCALAPPDATA%\\HSA\\Logs",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
