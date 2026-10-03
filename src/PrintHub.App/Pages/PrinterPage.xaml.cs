using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Printing;

namespace PrintHub.App.Pages;

public sealed partial class PrinterPage : Page
{
    bool _loading;
    public PrinterPage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged; App.State.StatusChanged += OnChanged;
        Refresh();
        _ = App.State.RefreshStatusAsync();
        _ = LoadJobsAsync();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged -= OnChanged; App.State.StatusChanged -= OnChanged;
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(Refresh);

    void Row(ref int row, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var k = new TextBlock { Text = key, Opacity = 0.7 };
        var v = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(k, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
        InfoGrid.Children.Add(k); InfoGrid.Children.Add(v);
        row++;
    }

    void Refresh()
    {
        var s = App.State; var d = s.Current; var st = s.Status;
        Heading.Text = d?.Name ?? "Printer";
        bool has = d is not null;
        foreach (var b in new Button[] { TestPageButton, RefreshButton }) b.IsEnabled = has;
        IdentifyButton.IsEnabled = s.Session?.Ipp is not null;
        bool win = d?.SpoolerName is not null;
        PrefsButton.IsEnabled = QueueButton.IsEnabled = PropsButton.IsEnabled = DefaultButton.IsEnabled = win;

        AlertsPanel.Children.Clear();
        if (s.StatusError is not null && st is null) AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Can't reach the printer", Message = s.StatusError });
        foreach (var a in st?.Alerts ?? Enumerable.Empty<string>())
            AlertsPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = a.StartsWith("Error") ? InfoBarSeverity.Error : InfoBarSeverity.Warning, Title = a });

        SuppliesPanel.Children.Clear();
        var supplies = st?.Supplies ?? new();
        foreach (var sup in supplies) SuppliesPanel.Children.Add(Ui.SupplyRow(sup));
        SuppliesNote.Text = supplies.Count == 0 ? "No supply information is available from this connection." : "";

        InfoGrid.Children.Clear(); InfoGrid.RowDefinitions.Clear();
        int r = 0;
        Row(ref r, "Status", s.StateText + (string.IsNullOrEmpty(st?.StateMessage) ? "" : " - " + st!.StateMessage));
        Row(ref r, "Model", st?.MakeAndModel is { Length: > 0 } m ? m : d?.Model);
        Row(ref r, "Manufacturer", d?.Manufacturer);
        Row(ref r, "Serial number", st?.SerialNumber);
        Row(ref r, "Firmware", st?.Firmware);
        Row(ref r, "Network address", d?.Address);
        Row(ref r, "Location", st?.Location);
        Row(ref r, "Speed", st?.PagesPerMinute is { } ppm ? $"{ppm} pages per minute" : null);
        Row(ref r, "Colour printing", st is null ? null : st.ColorSupported ? "Yes" : "No");
        Row(ref r, "Two-sided printing", st is null ? null : st.SupportsDuplex ? "Yes" : "No");
        Row(ref r, "Print formats", st is null ? null : string.Join(", ", st.DocumentFormats.Where(f => f.StartsWith("application/pdf") || f.StartsWith("image/")).Take(6)));
        Row(ref r, "Paper loaded", st is null ? null : string.Join(", ", st.MediaReady.Take(4)));
        Row(ref r, "Windows queue", d?.SpoolerName is null ? null : $"{d.SpoolerName} ({s.Queue?.StatusText ?? "…"}) driver: {d.SpoolerDriver}");
        Row(ref r, "Scanner", d is null ? null : d.EsclUri is not null ? "Network scanning (eSCL)" : d.WiaDeviceId is not null ? "Windows scanner (WIA)" : d.HasUsbHttp ? "USB web services" : "Not available");

        var c = new List<string>();
        if (d?.HasNetwork == true) c.Add($"Network: {d.IppUri ?? d.EsclUri}");
        if (d?.HasUsbHttp == true) c.Add($"USB: {d.Usb?.Describe() ?? d.UsbCandidates.FirstOrDefault(u => u.Openable)?.Describe()}");
        if (win) c.Add($"Windows print queue on port {d!.SpoolerPort}");
        if (d?.WiaDeviceId is not null) c.Add("Windows scanner driver (WIA)");
        c.Add(s.Session?.ViaUsb == true ? "Currently talking to the printer over USB." : s.Session?.Ipp is not null || s.Session?.Escl is not null ? "Currently talking to the printer over the network." : "");
        ConnectionText.Text = string.Join(Environment.NewLine, c.Where(x => x.Length > 0));

        _loading = true;
        PreferUsbSwitch.IsOn = s.Settings.PreferUsb;
        PreferUsbSwitch.IsEnabled = d?.HasUsbHttp == true && d.HasNetwork;
        _loading = false;
        JobsCard.Visibility = s.Session?.Ipp is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    async void PreferUsb_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.State.Settings.PreferUsb = PreferUsbSwitch.IsOn; App.State.SaveSettings();
        if (App.State.Current is { } d) await App.State.SelectAsync(d);
    }

    async Task LoadJobsAsync()
    {
        JobsPanel.Children.Clear();
        var ipp = App.State.Session?.Ipp;
        if (ipp is null) return;
        try
        {
            var jobs = await ipp.GetJobsAsync();
            if (jobs.Count == 0) JobsPanel.Children.Add(new TextBlock { Text = "No jobs are waiting.", Opacity = 0.7 });
            foreach (var j in jobs)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = $"#{j.Id}  {j.Name}  ·  {j.State}  ·  {j.User}", VerticalAlignment = VerticalAlignment.Center });
                var cancel = new Button { Content = "Cancel job" };
                int id = j.Id;
                cancel.Click += async (_, _) =>
                {
                    try { await ipp.CancelJobAsync(id); await LoadJobsAsync(); }
                    catch (Exception ex) { App.Window.Toast("Could not cancel the job: " + ex.Message, InfoBarSeverity.Warning); }
                };
                Grid.SetColumn(cancel, 1); row.Children.Add(cancel);
                JobsPanel.Children.Add(row);
            }
        }
        catch (Exception ex) { JobsPanel.Children.Add(new TextBlock { Text = "This printer didn't return its job list (" + ex.Message + ")", Opacity = 0.7, TextWrapping = TextWrapping.Wrap }); }
    }

    async void Jobs_Click(object sender, RoutedEventArgs e) => await LoadJobsAsync();
    async void Refresh_Click(object sender, RoutedEventArgs e) { await App.State.RefreshStatusAsync(); await LoadJobsAsync(); App.Window.Toast("Status updated."); }

    async void TestPage_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State; if (s.Current is null) return;
        TestPageButton.IsEnabled = false;
        try
        {
            await PrinterTools.PrintTestPageAsync(s.Current, s.Session, s.Settings.PrintRoute);
            App.Window.Toast("Test page sent to the printer.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { AppLog.Write("Test page: " + ex); App.Window.Toast("Test page failed: " + ex.Message, InfoBarSeverity.Error); }
        finally { TestPageButton.IsEnabled = true; }
    }

    async void Identify_Click(object sender, RoutedEventArgs e)
    {
        try { await PrinterTools.IdentifyAsync(App.State.Session!); App.Window.Toast("The printer should be flashing or beeping now.", InfoBarSeverity.Success); }
        catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); }
    }

    void Prefs_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenPreferences(App.State.Current!.SpoolerName!));
    void Queue_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenQueue(App.State.Current!.SpoolerName!));
    void Props_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenProperties(App.State.Current!.SpoolerName!));
    void Web_Click(object sender, RoutedEventArgs e) => App.Window.NavigateTo("web");

    void Default_Click(object sender, RoutedEventArgs e)
    {
        var name = App.State.Current?.SpoolerName;
        if (name is not null && SpoolerPrinters.SetDefault(name)) App.Window.Toast($"{name} is now the default printer.", InfoBarSeverity.Success);
        else App.Window.Toast("Windows refused to change the default printer.", InfoBarSeverity.Warning);
    }

    async void Report_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State; if (s.Current is null) return;
        try
        {
            var text = await PrinterTools.BuildReportAsync(s.Current, s.Session);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            App.Window.Toast("Support report copied to the clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { App.Window.Toast("Could not build the report: " + ex.Message, InfoBarSeverity.Error); }
    }

    static void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); }
    }
}
