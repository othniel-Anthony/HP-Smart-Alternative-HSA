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

            SuppliesPanel.Children.Clear();
            var supplies = s.Status?.Supplies.Where(x => !x.IsWaste).ToList() ?? new();
            foreach (var sup in supplies) SuppliesPanel.Children.Add(Ui.SupplyRow(sup));
            SuppliesCard.Visibility = Visibility.Visible;
            SuppliesNote.Text = supplies.Count > 0 ? ""
                : s.Status is not null ? "This printer doesn't report ink or toner levels over the network."
                : d.SpoolerName is not null && s.Session?.Ipp is null ? "Supply levels need a network or USB (IPP) connection. This printer is only connected through a Windows driver."
                : s.Connecting ? "Connecting…" : "Supply levels will appear when the printer responds.";
        }

        AlertsPanel.Children.Clear();
        if (s.StatusError is not null && s.Status is null)
            AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Can't reach the printer", Message = s.StatusError });
        foreach (var a in s.Status?.Alerts ?? Enumerable.Empty<string>())
            AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = a.StartsWith("Error") ? InfoBarSeverity.Error : InfoBarSeverity.Warning, Title = a });
        foreach (var low in s.Status?.Supplies.Where(x => x.IsLow && !x.IsWaste) ?? Enumerable.Empty<Core.Ipp.SupplyLevel>())
            AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = $"{low.Name} is low ({low.Percent}%)" });

        BuildTiles(d);
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
        if (button is not null) button.IsEnabled = false;
        App.Window.Toast($"Sending the colour test page to {s.Current.Name}…");
        try
        {
            await Core.Printing.PrinterTools.PrintBundledTestPageAsync(s.Current, s.Session, s.Settings.PrintRoute);
            App.Window.Toast($"Test page sent to {s.Current.Name}.", InfoBarSeverity.Success);
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
