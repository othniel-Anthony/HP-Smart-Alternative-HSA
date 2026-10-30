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
    HpWirelessState? _wireless;
    bool _probing;

    public HpMaintenancePage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged; App.State.StatusChanged += OnStatus;
        _ = ProbeAsync();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; App.State.StatusChanged -= OnStatus; }
    void OnChanged() => DispatcherQueue.TryEnqueue(() => { _info = null; _wireless = null; Update(); _ = ProbeAsync(); });
    void OnStatus() => DispatcherQueue.TryEnqueue(ShowSupplies);

    async Task ProbeAsync()
    {
        if (_probing) return;
        var s = App.State;
        if (s.Current?.IsHp != true || s.Connecting) { Update(); return; }
        _probing = true; Update();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var http = s.Session?.CreateWebServicesClient();
                    if (http is null || s.Session?.HttpBase is not { } baseUri) { _info = new HpMaintenanceInfo { Problem = NoServicesMessage(s) }; return; }
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    _info = await HpMaintenance.ProbeAsync(http, baseUri, cts.Token);
                    // Wi-Fi Direct settings exist only on printers that serve the JSON web services
                    if (_info.Reachable && _info.Protocol == HpProtocol.Cdm) _wireless = await HpWireless.ReadAsync(http, baseUri, cts.Token);
                    break;
                }
                catch (Exception ex) when (attempt == 0 && IsLostLink(ex)) { AppLog.Write("HP maintenance probe: the link was closed under it; trying again: " + ex.Message); await WaitForReconnectAsync(); }
            }
        }
        catch (Exception ex) { AppLog.Write("HP maintenance probe: " + ex); _info = new HpMaintenanceInfo { Problem = Friendly(ex) }; }
        finally { _probing = false; BuildMenus(); Update(); }
    }

    /// <summary>
    /// The connection to HSA's own local USB bridge (127.0.0.1) was refused: the session behind it was closed or replaced while a request was on its way
    /// (the printer was re-detected, replugged or switched to another connection). The printer itself did not refuse anything.
    /// </summary>
    static bool IsLostLink(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused }) return true;
        return false;
    }

    static string Friendly(Exception ex) => IsLostLink(ex)
        ? "HSA lost its connection to the printer while it was working (the USB link was closed or re-opened, for example when the printer was re-detected). The printer itself refused nothing, and a page it had already started carries on by itself. Wait a few seconds, check the printer, and try again."
        : ex.Message;

    /// <summary>Waits (up to 20 s) for the app to finish re-opening the printer's connection after it was replaced.</summary>
    static async Task WaitForReconnectAsync()
    {
        for (int i = 0; i < 40 && (App.State.Connecting || App.State.Session is null); i++) await Task.Delay(500);
        await Task.Delay(500);
    }

    static string NoServicesMessage(AppState s) =>
        s.Current?.UsbCandidates.Any(u => u.Present && !u.Openable) == true
            ? "This printer's USB web-services interface is held by another driver, so HSA cannot use it over USB. Connect the printer to your network."
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
        ShowWireless(can);
        ShowSupplies();
    }

    void ShowWireless(bool can)
    {
        var w = _wireless;
        WirelessCard.Visibility = w is null || App.State.Current?.IsHp != true ? Visibility.Collapsed : Visibility.Visible;
        if (w is null) return;
        DirectText.Text = w.DirectOn
            ? "On" + (w.DirectSsid.Length > 0 ? $": network name “{w.DirectSsid}”" : "") + (w.DirectAddress is not null ? $", printer address {w.DirectAddress}" : "")
            : "Off";
        StationText.Text = $"Wi-Fi (joined to a network): {w.StationText}." + (w.DirectInSetupMode && !w.DirectOn ? " The printer is still in its first-time setup state." : "");
        DirectButton.Content = w.DirectOn ? "Turn off" : "Turn on";
        DirectButton.IsEnabled = can;
    }

    async void Direct_Click(object sender, RoutedEventArgs e)
    {
        var w = _wireless; if (w is null) return;
        bool on = !w.DirectOn;
        bool overWifi = App.State.Session?.ViaUsb != true;
        string text = on
            ? "The printer starts its own Wi-Fi network so a phone or computer can print to it without a router. It keeps its normal Wi-Fi connection."
            : "The printer stops its own Wi-Fi network. Anything connected to it directly will lose the connection. Its normal Wi-Fi connection is not changed.";
        if (overWifi && !on) text += " HSA is connected to this printer over the network: if that is through Wi-Fi Direct, HSA will lose it too.";
        if (!await Ui.ConfirmAsync(XamlRoot, on ? "Turn Wi-Fi Direct on?" : "Turn Wi-Fi Direct off?", text, on ? "Turn on" : "Turn off")) return;
        await RunAsync(on ? "Turning Wi-Fi Direct on…" : "Turning Wi-Fi Direct off…",
            (http, baseUri, progress, ct) => HpWireless.SetDirectAsync(http, baseUri, on, progress, ct), TimeSpan.FromMinutes(2));
        // show what the printer reports now, whether or not the change went through
        try
        {
            if (App.State.Session?.CreateWebServicesClient() is { } http2 && App.State.Session.HttpBase is { } b)
            {
                using (http2) { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); _wireless = await HpWireless.ReadAsync(http2, b, cts.Token) ?? _wireless; }
            }
        }
        catch (Exception ex) { AppLog.Write("HP Wi-Fi Direct re-read: " + ex.Message); }
        Update();
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

    async Task<bool> RunAsync(string startText, Func<HttpClient, Uri, IProgress<string>, CancellationToken, Task<string>> work, TimeSpan timeout)
    {
        var s = App.State;
        if (s.Session?.CreateWebServicesClient() is null || s.Session.HttpBase is null) return false;
        _busy = true; Update(); Ring.IsActive = true; ResultText.Text = startText;

        // always the connection the app has now: it may have been re-opened since the last try
        async Task<string> AttemptAsync()
        {
            var session = App.State.Session;
            if (session?.CreateWebServicesClient() is not { } http || session.HttpBase is not { } baseUri)
                throw new InvalidOperationException("The printer is not connected right now. Wait for HSA to reconnect and try again.");
            using (http)
            {
                using var cts = new CancellationTokenSource(timeout);
                var progress = new Progress<string>(t => ResultText.Text = t);
                return await work(http, baseUri, progress, cts.Token);
            }
        }
        try
        {
            ResultText.Text = await AttemptAsync();   // not repeated on a lost link: a job that had started would be sent twice
            App.Window.Toast(ResultText.Text, InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException) { ResultText.Text = "The printer took too long to answer."; return false; }
        catch (Exception ex)
        {
            AppLog.Write("HP maintenance: " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", Friendly(ex));
            return false;
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
        var info = _info; var mode = info?.AlignmentMode; if (info is null || mode is null) return;
        if (info.Protocol == HpProtocol.Cdm)
        {
            // print the alignment page, let the user put it on the glass, then have the printer scan it
            var page = info.Jobs.FirstOrDefault(j => j.JobType.Equals("alignmentPage", StringComparison.OrdinalIgnoreCase));
            if (page is null) return;
            if (!await Ui.ConfirmAsync(XamlRoot, "Align the printhead?", "The printer prints an alignment page. Then you put it face down on the scanner glass, close the lid, and the printer scans it. Make sure plain paper is loaded.", "Print page")) return;
            bool printed = await RunAsync("Printing the alignment page…", async (http, baseUri, progress, ct) =>
            {
                await HpMaintenance.RunJobAsync(http, baseUri, page, progress, ct);
                return "The alignment page is printed.";
            }, TimeSpan.FromMinutes(6));
            if (!printed) return;
            if (!await Ui.ConfirmAsync(XamlRoot, "Scan the alignment page", "Put the printed page face down on the scanner glass, close the lid, then press Scan.", "Scan")) { ResultText.Text = "Alignment not finished: the page was printed but not scanned."; return; }
            await RunAsync("Scanning the alignment page…", async (http, baseUri, progress, ct) =>
            {
                await HpMaintenance.StartAlignmentAsync(http, baseUri, mode, progress, ct, protocol: info.Protocol);
                return "The alignment finished. Print a test page to check the result.";
            }, TimeSpan.FromMinutes(10));
            return;
        }
        string explain = mode switch
        {
            "automatic" => "The printer prints an alignment page and aligns itself. Make sure plain paper is loaded.",
            "semiAutomatic" => "The printer prints an alignment page. Then you put it face down on the scanner glass and follow the prompt on the printer. Make sure plain paper is loaded.",
            _ => "This printer needs you to pick the best pattern on a printed page, which HSA does not do yet. You will get the details after pressing Start.",
        };
        if (!await Ui.ConfirmAsync(XamlRoot, "Align the printhead?", explain, "Start")) return;
        await RunAsync("Aligning the printhead…", (http, baseUri, progress, ct) => HpMaintenance.StartAlignmentAsync(http, baseUri, mode, progress, ct, protocol: info.Protocol), TimeSpan.FromMinutes(10));
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
    void Drivers_Click(object sender, RoutedEventArgs e) => Ui.OpenDrivers();
    void Driver_Click(object sender, RoutedEventArgs e)
    {
        try { SpoolerPrinters.OpenPreferences(App.State.Current!.SpoolerName!); } catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); }
    }
}
