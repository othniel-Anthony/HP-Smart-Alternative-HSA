using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Printing;

namespace PrintHub.App.Pages;

/// <summary>HP maintenance (ink levels, printhead cleaning and alignment, report pages) through the printer's web services, over USB or the network. Only offered for HP printers.</summary>
public sealed partial class HpMaintenancePage : Page
{
    bool _busy;
    HpMaintenanceInfo? _info;
    bool _probing;

    public HpMaintenancePage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged; App.State.StatusChanged += OnStatus;
        _ = ProbeAsync();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; App.State.StatusChanged -= OnStatus; }
    void OnChanged() => DispatcherQueue.TryEnqueue(() => { _info = null; Update(); _ = ProbeAsync(); });
    void OnStatus() => DispatcherQueue.TryEnqueue(ShowSupplies);

    async Task ProbeAsync()
    {
        if (_probing) return;
        var s = App.State;
        if (s.Current?.IsHp != true || s.Connecting) { Update(); return; }
        _probing = true; Update();
        try
        {
            using var http = s.Session?.CreateWebServicesClient();
            if (http is null || s.Session?.HttpBase is not { } baseUri) { _info = new HpMaintenanceInfo { Problem = NoServicesMessage(s) }; return; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            _info = await HpMaintenance.ProbeAsync(http, baseUri, cts.Token);
        }
        catch (Exception ex) { AppLog.Write("HP maintenance probe: " + ex); _info = new HpMaintenanceInfo { Problem = ex.Message }; }
        finally { _probing = false; BuildMenus(); Update(); }
    }

    static string NoServicesMessage(AppState s) =>
        s.Current?.UsbCandidates.Any(u => u.Present && !u.Openable) == true
            ? "This printer's USB web-services interface is held by another driver, so HSA cannot use it over USB. Connect the printer to your network, or use HP Smart."
            : "HSA has no way to talk to this printer's web services (no network address and no usable USB interface). Connect it by USB or Wi-Fi and refresh.";

    void BuildMenus()
    {
        CleanMenu.Items.Clear(); ReportMenu.Items.Clear();
        if (_info is not { Reachable: true } info) return;
        foreach (var j in info.CleaningJobs)
        {
            var item = new MenuFlyoutItem { Text = j.Title, Tag = j };
            AutomationProperties.SetName(item, $"Cleaning level {j.Level}");
            item.Click += Clean_Click;
            CleanMenu.Items.Add(item);
        }
        foreach (var j in info.Reports)
        {
            var item = new MenuFlyoutItem { Text = j.Title, Tag = j };
            AutomationProperties.SetName(item, j.Title);
            item.Click += Report_Click;
            ReportMenu.Items.Add(item);
        }
    }

    void Update()
    {
        var s = App.State; var d = s.Current;
        Heading.Text = d is null ? "HP maintenance" : $"HP maintenance: {d.Name}";
        bool hp = d?.IsHp == true;
        bool ready = _info is { Reachable: true };

        Notice.IsOpen = true;
        if (!hp) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = "Choose an HP printer"; Notice.Message = "These tools are for HP printers."; }
        else if (_probing || s.Connecting) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = "Checking what this printer supports…"; Notice.Message = ""; }
        else if (_info is { Problem: not null }) { Notice.Severity = InfoBarSeverity.Warning; Notice.Title = "Not available"; Notice.Message = _info.Problem; }
        else Notice.IsOpen = false;

        bool can = ready && !_busy;
        CleanMenuButton.IsEnabled = can && CleanMenu.Items.Count > 0;
        AlignButton.IsEnabled = can && _info!.CanAlign;
        ReportMenuButton.IsEnabled = can && ReportMenu.Items.Count > 0;
        RefreshButton.IsEnabled = hp && !_busy;
        WebButton.IsEnabled = hp;
        DriverButton.IsEnabled = d?.SpoolerName is not null;
        ReportCopyButton.IsEnabled = hp && s.Session?.HttpBase is not null;
        ShowSupplies();
    }

    void ShowSupplies()
    {
        var s = App.State;
        SuppliesPanel.Children.Clear();
        var supplies = s.Status?.Supplies.Where(x => !x.IsWaste).ToList() ?? new();
        foreach (var sup in supplies) SuppliesPanel.Children.Add(Ui.SupplyRow(sup));
        string source = s.Session?.ViaUsb == true ? "read over the USB cable" : "read over the network";
        SuppliesNote.Text = supplies.Count > 0 ? $"Levels {source}." :
            s.Connecting ? "Connecting…" :
            "This printer has not reported ink levels yet. Press Refresh; if they never appear, “Copy diagnostics” below collects what the printer sends so it can be looked at.";
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        await App.State.RefreshStatusAsync();
        Update();
        App.Window.Toast("Ink levels updated.");
    }

    // ------------------------------------------------------------------ doing things

    async Task RunAsync(string startText, Func<HttpClient, Uri, IProgress<string>, CancellationToken, Task<string>> work, TimeSpan timeout)
    {
        var s = App.State;
        if (s.Session?.CreateWebServicesClient() is not { } http || s.Session.HttpBase is not { } baseUri) return;
        _busy = true; Update(); Ring.IsActive = true; ResultText.Text = startText;
        try
        {
            using (http)
            {
                using var cts = new CancellationTokenSource(timeout);
                var progress = new Progress<string>(t => ResultText.Text = t);
                ResultText.Text = await work(http, baseUri, progress, cts.Token);
            }
            App.Window.Toast(ResultText.Text, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { ResultText.Text = "The printer took too long to answer."; }
        catch (Exception ex)
        {
            AppLog.Write("HP maintenance: " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", ex.Message);
        }
        finally { _busy = false; Ring.IsActive = false; Update(); _ = App.State.RefreshStatusAsync(); }
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var job = (HpJob)((FrameworkElement)sender).Tag;
        bool ok = await Ui.ConfirmAsync(XamlRoot, $"{job.Title}?",
            "The printer prints a cleaning page, which uses ink and takes a few minutes" + (job.Level > 1 ? " (more for the higher levels)" : "") +
            ". Make sure plain paper is loaded. Try level 1 first, and only go up if lines are still missing.", "Clean");
        if (!ok) return;
        await RunAsync($"{job.Title}. Please wait…", async (http, baseUri, progress, ct) =>
        {
            await HpMaintenance.RunJobAsync(http, baseUri, job, progress, ct);
            return "Cleaning finished. Print a report page to see the result.";
        }, TimeSpan.FromMinutes(15));
    }

    async void Report_Click(object sender, RoutedEventArgs e)
    {
        var job = (HpJob)((FrameworkElement)sender).Tag;
        await RunAsync($"Printing: {job.Title}…", async (http, baseUri, progress, ct) =>
        {
            var how = await HpMaintenance.RunJobAsync(http, baseUri, job, progress, ct);
            return how == "finished" ? $"{job.Title} printed." : $"{job.Title} was sent to the printer.";
        }, TimeSpan.FromMinutes(6));
    }

    async void Align_Click(object sender, RoutedEventArgs e)
    {
        var mode = _info?.AlignmentMode; if (mode is null) return;
        string explain = mode switch
        {
            "automatic" => "The printer prints an alignment page and aligns itself. Make sure plain paper is loaded.",
            "semiAutomatic" => "The printer prints an alignment page. Then you put it face down on the scanner glass and follow the prompt on the printer. Make sure plain paper is loaded.",
            _ => "This printer needs you to pick the best pattern on a printed page, which HSA does not do yet. You will get the details after pressing Start.",
        };
        if (!await Ui.ConfirmAsync(XamlRoot, "Align the printhead?", explain, "Start")) return;
        await RunAsync("Aligning the printhead…", (http, baseUri, progress, ct) => HpMaintenance.StartAlignmentAsync(http, baseUri, mode, progress, ct), TimeSpan.FromMinutes(10));
    }

    async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State;
        if (s.Current is null) return;
        try
        {
            var text = await PrinterTools.BuildReportAsync(s.Current, s.Session);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            App.Window.Toast("Diagnostics copied to the clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { App.Window.Toast("Could not build the diagnostics: " + ex.Message, InfoBarSeverity.Error); }
    }

    void Web_Click(object sender, RoutedEventArgs e) => App.Window.NavigateTo("web");
    void Driver_Click(object sender, RoutedEventArgs e)
    {
        try { SpoolerPrinters.OpenPreferences(App.State.Current!.SpoolerName!); } catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); }
    }
}
