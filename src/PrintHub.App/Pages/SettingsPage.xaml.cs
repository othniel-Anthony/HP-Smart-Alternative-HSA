using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Imaging;
using PrintHub.Core.Printing;
using PrintHub.Core.Settings;
using PrintHub.Core.Usb;

namespace PrintHub.App.Pages;

public sealed partial class SettingsPage : Page
{
    bool _loading;

    public SettingsPage() => InitializeComponent();

    AppSettings S => App.State.Settings;

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        Ui.Fill(ThemeCombo, new[] { ("Use system setting", "System"), ("Light", "Light"), ("Dark", "Dark") }, S.Theme);
        Ui.Fill(RouteCombo, new[] { ("Automatic (driver if installed, otherwise direct)", PrintRoute.Auto), ("Always the Windows printer driver", PrintRoute.WindowsDriver), ("Always directly (IPP)", PrintRoute.DirectIpp) }, S.PrintRoute);
        UsbSwitch.IsOn = S.PreferUsb;
        OpenFolderCheck.IsChecked = S.OpenFolderAfterSave;
        SearchableCheck.IsChecked = S.SearchablePdf;
        _loading = false;

        FolderBox.Text = S.EffectiveSaveFolder;
        OcrNote.Text = OcrService.IsAvailable
            ? "Text recognition is available: " + string.Join(", ", OcrService.Languages)
            : "Text recognition is not available: install a language with OCR support in Windows Settings > Time & language > Language & region.";
        AboutText.Text = $"HP Smart Alternative (HSA) {AppState.Version}\nAn alternative to HP Smart for Windows. Works with any printer that supports IPP / eSCL or has a Windows driver, over network and USB.\nSettings: {SettingsStore.FilePath}";
        BuildSaved();
        RescanUsb();
    }

    void Save() { if (!_loading) App.State.SaveSettings(); }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Theme = Ui.Selected<string>(ThemeCombo) ?? "System"; Save(); App.Window.ApplyTheme(S.Theme);
    }

    void Route_Changed(object sender, SelectionChangedEventArgs e) { if (_loading) return; S.PrintRoute = Ui.Selected<PrintRoute>(RouteCombo); Save(); }

    async void Usb_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.PreferUsb = UsbSwitch.IsOn; Save();
        if (App.State.Current is { } d) await App.State.SelectAsync(d);
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.OpenFolderAfterSave = OpenFolderCheck.IsChecked == true; S.SearchablePdf = SearchableCheck.IsChecked == true; Save();
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = await Ui.PickFolderAsync();
        if (f is null) return;
        S.SaveFolder = f; Save(); FolderBox.Text = f;
    }

    void DefaultFolder_Click(object sender, RoutedEventArgs e) { S.SaveFolder = null; Save(); FolderBox.Text = S.EffectiveSaveFolder; }
    void OpenFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenFile(S.EffectiveSaveFolder);
    void OpenLogs_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(AppLog.Folder); Ui.OpenFile(AppLog.Folder); }
    void OpenSettings_Click(object sender, RoutedEventArgs e) => Ui.OpenFile(Path.GetDirectoryName(SettingsStore.FilePath)!);

    void BuildSaved()
    {
        SavedPanel.Children.Clear();
        SavedNone.Visibility = S.Printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in S.Printers.ToList())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{p.Name}   ({p.Address})", VerticalAlignment = VerticalAlignment.Center });
            var rm = new Button { Content = "Remove" };
            rm.Click += (_, _) =>
            {
                S.Printers.Remove(p); Save();
                App.State.Devices.RemoveAll(d => d.Id == p.Id && !d.HasUsbHttp && d.SpoolerName is null);
                BuildSaved(); _ = App.State.DiscoverAsync();
            };
            Grid.SetColumn(rm, 1); row.Children.Add(rm);
            SavedPanel.Children.Add(row);
        }
    }

    void RescanUsb_Click(object sender, RoutedEventArgs e) => RescanUsb();

    void RescanUsb()
    {
        try
        {
            var all = UsbDeviceScanner.Scan(presentOnly: false).Where(i => i.HttpKind != UsbHttpKind.None).OrderByDescending(i => i.Present).ToList();
            UsbBox.Text = all.Count == 0
                ? "No printers with a USB web interface were found. Connect the printer with a USB cable and switch it on."
                : string.Join(Environment.NewLine, all.Select(i => (i.Present ? "● " : "○ ") + i.Describe())) +
                  Environment.NewLine + Environment.NewLine + "● connected now   ○ remembered by Windows but not connected";
        }
        catch (Exception ex) { UsbBox.Text = "Could not read USB devices: " + ex.Message; }
    }
}
