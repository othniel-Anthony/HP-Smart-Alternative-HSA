using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;

namespace PrintHub.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged;
        App.State.StatusChanged += OnChanged;
        App.State.DevicesChanged += OnChanged;
        Refresh();
        _ = App.State.RefreshStatusAsync();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged -= OnChanged;
        App.State.StatusChanged -= OnChanged;
        App.State.DevicesChanged -= OnChanged;
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(Refresh);

    // The status is refreshed every 20 seconds and each refresh raises StatusChanged. Tearing down and rebuilding the tiles, supply rows and
    // alerts every time made the whole page flicker when it was simply left open, so each part is rebuilt only when what it shows has changed.
    string? _suppliesKey, _alertsKey, _tilesKey;

    void Refresh()
    {
        var s = App.State; var d = s.Current;
        EmptyCard.Visibility = d is null && !s.Discovering ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.IsEnabled = d is not null;
        FindButton.IsEnabled = !s.Discovering;

        if (d is null)
        {
            NameText.Text = s.Discovering ? "Looking for printers…" : "No printer selected";
            DetailText.Text = s.Devices.Count > 0 ? "Pick a printer from the list at the top." : "";
            StateLine.Text = "";
            SuppliesCard.Visibility = Visibility.Collapsed;
            _suppliesKey = null;
        }
        else
        {
            NameText.Text = d.Name;
            var parts = new List<string>();
            if (d.Connection.Length > 0) parts.Add(d.Connection + (s.Session?.ViaUsb == true ? " (using USB now)" : ""));
            if (d.Address is not null) parts.Add(d.Address);
            if (s.Status?.Firmware is { Length: > 0 } fw) parts.Add("firmware " + fw);
            DetailText.Text = string.Join("  ·  ", parts);
            StateLine.Text = s.StateText;

            var supplies = s.Status?.Supplies.Where(x => !x.IsWaste).ToList() ?? new();
            var suppliesKey = d.Id + "|" + string.Join(";", supplies.Select(x => $"{x.Name}:{x.Percent}:{x.IsLow}"));
            if (suppliesKey != _suppliesKey)
            {
                _suppliesKey = suppliesKey;
                SuppliesPanel.Children.Clear();
                foreach (var sup in supplies) SuppliesPanel.Children.Add(Ui.SupplyRow(sup));
            }
            SuppliesCard.Visibility = Visibility.Visible;
            SuppliesNote.Text = supplies.Count > 0 ? ""
                : s.Status is not null ? "This printer doesn't report ink or toner levels over this connection."
                : d.SpoolerName is not null && s.Session?.Ipp is null && !s.Connecting ? "HSA can't read ink levels over this USB cable: the printer doesn't offer a standard USB web service (IPP-USB or HP web services). If it also has Wi-Fi or Ethernet, the levels appear once it is found on the network."
                : s.Connecting ? "Connecting…" : "Supply levels will appear when the printer responds.";
        }

        var lows = (s.Status?.Supplies.Where(x => x.IsLow && !x.IsWaste) ?? Enumerable.Empty<Core.Ipp.SupplyLevel>()).ToList();
        var alerts = (s.Status?.Alerts ?? Enumerable.Empty<string>()).ToList();
        var alertsKey = (s.StatusError is not null && s.Status is null ? s.StatusError : "") + "|" + string.Join(";", alerts) + "|" + string.Join(";", lows.Select(l => $"{l.Name}:{l.Percent}"));
        if (alertsKey != _alertsKey)
        {
            _alertsKey = alertsKey;
            AlertsPanel.Children.Clear();
            if (s.StatusError is not null && s.Status is null)
                AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Can't reach the printer", Message = s.StatusError });
            foreach (var a in alerts)
                AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = a.StartsWith("Error") ? InfoBarSeverity.Error : InfoBarSeverity.Warning, Title = a });
            foreach (var low in lows)
                AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = $"{low.Name} is low ({low.Percent}%)" });
        }

        var tilesKey = $"{d?.Id}|{d?.CanPrint}|{d?.CanScan}";
        if (tilesKey != _tilesKey) { _tilesKey = tilesKey; BuildTiles(d); }
    }

    void BuildTiles(Core.Discovery.PrinterDevice? d)
    {
        Tiles.Items.Clear();
        bool print = d?.CanPrint == true, scan = d?.CanScan == true;
        Tiles.Items.Add(Ui.Tile("", "Print", print ? "Documents and photos" : "Not available", (_, _) => App.Window.NavigateTo("print"), d is not null, "Sky"));
        Tiles.Items.Add(Ui.Tile("", "Scan", scan ? "Flatbed or feeder" : "No scanner found", (_, _) => App.Window.NavigateTo("scan"), d is not null, "Mint"));
        Tiles.Items.Add(Ui.Tile("", "Copy", scan && print ? "Scan and print" : "Needs print and scan", (_, _) => App.Window.NavigateTo("copy"), d is not null, "Lavender"));
        Tiles.Items.Add(Ui.Tile("", "Shortcuts", "One-tap workflows", (_, _) => App.Window.NavigateTo("shortcuts"), d is not null, "Butter"));
        Tiles.Items.Add(Ui.Tile("", "Print test page", print ? "Colour test page" : "Not available", TestPage_Click, print, "Pink"));
        Tiles.Items.Add(Ui.Tile("", "Printer web page", "Full printer settings", (_, _) => App.Window.NavigateTo("web"), d is not null, "Peach"));
    }

    /// <summary>Prints the bundled colour test page (print-color-test-page-basic-1.pdf) exactly as it is.</summary>
    async void TestPage_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State;
        if (s.Current is null) return;
        var button = sender as Button;

        // how many copies? (the last number is remembered, so a repeat is one click and Enter)
        var box = new NumberBox { Minimum = 1, Maximum = Core.Printing.PrinterTools.MaxTestPageCopies, Value = Math.Clamp(s.Settings.TestPageCopies, 1, Core.Printing.PrinterTools.MaxTestPageCopies),
                                  SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1, LargeChange = 5, Width = 160 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Copies");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"How many copies of the colour test page should {s.Current.Name} print?", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Print test page", Content = panel, PrimaryButtonText = "Print", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        int copies = double.IsNaN(box.Value) ? 1 : (int)Math.Clamp(Math.Round(box.Value), 1, Core.Printing.PrinterTools.MaxTestPageCopies);
        s.Settings.TestPageCopies = copies; s.SaveSettings();

        if (button is not null) button.IsEnabled = false;
        string what = copies == 1 ? "the colour test page" : $"{copies} copies of the colour test page";
        App.Window.Toast($"Sending {what} to {s.Current.Name}…");
        try
        {
            await Core.Printing.PrinterTools.PrintBundledTestPageAsync(s.Current, s.Session, s.Settings.PrintRoute, copies);
            App.Window.Toast(copies == 1 ? $"Test page sent to {s.Current.Name}." : $"{copies} test pages sent to {s.Current.Name}.", InfoBarSeverity.Success);
            _ = s.RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Home test page: " + ex);
            await Ui.MessageAsync(XamlRoot, "Couldn't print the test page", ex.Message);
        }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    void Details_Click(object sender, RoutedEventArgs e) => App.Window.NavigateTo("printer");
    async void Find_Click(object sender, RoutedEventArgs e) => await App.State.DiscoverAsync();
}
