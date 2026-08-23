using System.Collections.ObjectModel;
using System.Windows.Media;
using HSA.Models;
using HSA.Native;
using HSA.Services;
using Microsoft.Extensions.Logging;

namespace HSA.ViewModels;

public sealed class PrintersViewModel : ObservableObject
{
    private readonly IPrinterService _printers;
    private readonly IDriverService _drivers;
    private readonly IFirmwareService _firmware;
    private readonly IModelImageService _modelImages;
    private readonly IConsumableService _consumables;
    private readonly PrinterEndpointDiscovery _discovery;
    private readonly EwsService _ews;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialog;
    private readonly ILogger<PrintersViewModel> _log;

    public ObservableCollection<PrinterInfo> Printers { get; } = new();
    public ObservableCollection<PrintJob> Jobs { get; } = new();
    public ObservableCollection<DiscoveredNetworkPrinter> Discovered { get; } = new();

    private PrinterInfo? _selectedPrinter;
    public PrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetField(ref _selectedPrinter, value))
            {
                // Re-publish EWS status — the indicator reads from the selected printer.
                OnPropertyChanged(nameof(EwsStatusText));
                OnPropertyChanged(nameof(EwsStatusShortText));
                OnPropertyChanged(nameof(EwsUrlDisplay));
                OnPropertyChanged(nameof(EwsStatusBrush));
                // The action commands' CanExecute is bound to SelectedPrinter is not null.
                // CommandManager only re-evaluates on input events; programmatic changes (e.g.
                // auto-selecting Printers[0] in RefreshAsync) don't fire a requery, so the
                // buttons would stay IsEnabled=False until the user clicks somewhere. Force
                // a requery here so the action buttons enable as soon as a row is picked.
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                _ = LoadJobsAsync();
            }
        }
    }

    /// <summary>
    /// Human-readable EWS configuration status for the selected printer, e.g.
    /// "Configured: http://192.168.1.99", "Not configured", or "Select a printer".
    /// Used as the tooltip on the short-status pill.
    /// </summary>
    public string EwsStatusText
    {
        get
        {
            if (SelectedPrinter is null) return "No printer selected";
            if (_settings.Current.EwsAddresses.TryGetValue(SelectedPrinter.DeviceId, out var url)
                && !string.IsNullOrWhiteSpace(url))
                return $"Configured: {url}";
            return "Not configured";
        }
    }

    /// <summary>Compact EWS status pill text (no URL — that's shown in a separate line).</summary>
    public string EwsStatusShortText
    {
        get
        {
            if (SelectedPrinter is null) return "—";
            if (_settings.Current.EwsAddresses.TryGetValue(SelectedPrinter.DeviceId, out var url)
                && !string.IsNullOrWhiteSpace(url))
                return "✓ Configured";
            return "Not set";
        }
    }

    /// <summary>Just the EWS URL (or empty) — shown below the status pill.</summary>
    public string EwsUrlDisplay
    {
        get
        {
            if (SelectedPrinter is null) return string.Empty;
            if (_settings.Current.EwsAddresses.TryGetValue(SelectedPrinter.DeviceId, out var url)
                && !string.IsNullOrWhiteSpace(url))
                return url;
            return "(no EWS URL — click 'Set EWS URL…' to add one)";
        }
    }

    /// <summary>Brush matching <see cref="EwsStatusShortText"/> — green when set, neutral otherwise.</summary>
    public Brush EwsStatusBrush
    {
        get
        {
            if (SelectedPrinter is null) return EwsNoneSelectedBrush;
            if (_settings.Current.EwsAddresses.TryGetValue(SelectedPrinter.DeviceId, out var url)
                && !string.IsNullOrWhiteSpace(url))
                return EwsConfiguredBrush;
            return EwsNotConfiguredBrush;
        }
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    // Cached brushes for the EWS status pill (avoid allocating on every getter access).
    private static readonly Brush EwsConfiguredBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC9));  // green 100
    private static readonly Brush EwsNotConfiguredBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE0, 0xEC)); // purple-grey 50
    private static readonly Brush EwsNoneSelectedBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEA, 0xEE)); // neutral

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    private bool _showOnlyHp = true;
    public bool ShowOnlyHp { get => _showOnlyHp; set { if (SetField(ref _showOnlyHp, value)) _ = RefreshAsync(); } }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SetAsDefaultCommand { get; }
    public RelayCommand OpenAdvancedPropertiesCommand { get; }
    public RelayCommand OpenPrintingPreferencesCommand { get; }
    public AsyncRelayCommand PrintTestPageCommand { get; }
    public AsyncRelayCommand PauseQueueCommand { get; }
    public AsyncRelayCommand ResumeQueueCommand { get; }
    public AsyncRelayCommand PurgeQueueCommand { get; }
    public AsyncRelayCommand DeletePrinterCommand { get; }
    public AsyncRelayCommand CancelJobCommand { get; }
    public AsyncRelayCommand PauseJobCommand { get; }
    public AsyncRelayCommand ResumeJobCommand { get; }
    public AsyncRelayCommand DetectFirmwareCommand { get; }
    public AsyncRelayCommand InstallDriverCommand { get; }
    public AsyncRelayCommand DiscoverNetworkPrintersCommand { get; }
    public RelayCommand OpenEwsCommand { get; }
    public RelayCommand ConfigureEwsCommand { get; }

    public PrintersViewModel(
        IPrinterService printers,
        IDriverService drivers,
        IFirmwareService firmware,
        IModelImageService modelImages,
        IConsumableService consumables,
        PrinterEndpointDiscovery discovery,
        EwsService ews,
        SettingsService settings,
        IDialogService dialog,
        ILogger<PrintersViewModel> log)
    {
        _printers = printers;
        _drivers = drivers;
        _firmware = firmware;
        _modelImages = modelImages;
        _consumables = consumables;
        _discovery = discovery;
        _ews = ews;
        _settings = settings;
        _dialog = dialog;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        // EWS status pill depends on _settings; re-publish when settings change.
        _settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(EwsStatusText));
            OnPropertyChanged(nameof(EwsStatusShortText));
            OnPropertyChanged(nameof(EwsUrlDisplay));
            OnPropertyChanged(nameof(EwsStatusBrush));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        SetAsDefaultCommand = new AsyncRelayCommand(
            async () => { if (SelectedPrinter is null) return; await _printers.SetAsDefaultAsync(SelectedPrinter.Name); await RefreshAsync(); },
            () => SelectedPrinter is not null);
        OpenAdvancedPropertiesCommand = new RelayCommand(
            _ => _ = OpenAdvancedAsync(),
            _ => SelectedPrinter is not null);
        OpenPrintingPreferencesCommand = new RelayCommand(
            _ => _ = OpenPreferencesAsync(),
            _ => SelectedPrinter is not null);
        PrintTestPageCommand = new AsyncRelayCommand(
            async () => { if (SelectedPrinter is not null) await _printers.PrintTestPageAsync(SelectedPrinter.Name); },
            () => SelectedPrinter is not null);
        PauseQueueCommand = new AsyncRelayCommand(
            async () => { if (SelectedPrinter is not null) { await _printers.PauseQueueAsync(SelectedPrinter.Name); await RefreshAsync(); } },
            () => SelectedPrinter is not null);
        ResumeQueueCommand = new AsyncRelayCommand(
            async () => { if (SelectedPrinter is not null) { await _printers.ResumeQueueAsync(SelectedPrinter.Name); await RefreshAsync(); } },
            () => SelectedPrinter is not null);
        PurgeQueueCommand = new AsyncRelayCommand(
            async () => {
                if (SelectedPrinter is null) return;
                if (!_dialog.ConfirmDestructive("Purge print queue",
                    $"Cancel and purge every job in '{SelectedPrinter.Name}'? This cannot be undone.",
                    "Purge")) return;
                await _printers.PurgeQueueAsync(SelectedPrinter.Name);
                await LoadJobsAsync();
            },
            () => SelectedPrinter is not null);
        DeletePrinterCommand = new AsyncRelayCommand(
            async () => {
                if (SelectedPrinter is null) return;
                if (!_dialog.ConfirmDestructive("Remove printer",
                    $"Remove '{SelectedPrinter.Name}' from this PC? You can re-add it later. " +
                    "This does NOT remove the driver.", "Remove")) return;
                await _printers.DeleteAsync(SelectedPrinter.Name);
                await RefreshAsync();
            },
            () => SelectedPrinter is not null);
        CancelJobCommand = new AsyncRelayCommand(CancelJobAsync, () => SelectedJob is not null);
        PauseJobCommand = new AsyncRelayCommand(PauseJobAsync, () => SelectedJob is not null);
        ResumeJobCommand = new AsyncRelayCommand(ResumeJobAsync, () => SelectedJob is not null);
        DetectFirmwareCommand = new AsyncRelayCommand(DetectFirmwareAsync, () => SelectedPrinter is not null);
        InstallDriverCommand = new AsyncRelayCommand(InstallDriverForSelectedAsync, () => SelectedPrinter is not null);
        DiscoverNetworkPrintersCommand = new AsyncRelayCommand(DiscoverNetworkPrintersAsync);
        OpenEwsCommand = new RelayCommand(
            _ => OpenEwsForSelected(),
            _ => SelectedPrinter is not null && HasConfiguredEws(SelectedPrinter));
        ConfigureEwsCommand = new RelayCommand(
            _ => ConfigureEwsForSelected(),
            _ => SelectedPrinter is not null);
    }

    private bool HasConfiguredEws(PrinterInfo p) =>
        _settings.Current.EwsAddresses.TryGetValue(p.DeviceId, out var url) && !string.IsNullOrWhiteSpace(url);

    private void OpenEwsForSelected()
    {
        if (SelectedPrinter is null) return;
        if (!_settings.Current.EwsAddresses.TryGetValue(SelectedPrinter.DeviceId, out var url)
            || string.IsNullOrWhiteSpace(url))
        {
            _dialog.ShowInfo("EWS not configured",
                "Click 'Set EWS URL...' to enter this printer's EWS address (e.g. http://192.168.1.99).");
            return;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url.TrimEnd('/') + "/",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Failed to open EWS", ex);
        }
    }

    private async void ConfigureEwsForSelected()
    {
        if (SelectedPrinter is null) return;
        var current = _settings.Current.EwsAddresses.TryGetValue(SelectedPrinter.DeviceId, out var u) ? u : string.Empty;
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            $"Enter the EWS URL for '{SelectedPrinter.Name}':\n\n" +
            "Usually this is the printer's network IP, e.g. http://192.168.1.99.\n" +
            "HSA will use this URL to read consumable state (CMYK levels, alerts).\n\n" +
            "Tip: open the EWS in a browser first, copy the URL from the address bar.",
            "Set EWS URL",
            current);
        if (string.IsNullOrWhiteSpace(input)) return;  // user cancelled
        var url = input.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _dialog.ShowError("Invalid URL", "URL must start with http:// or https://");
            return;
        }
        // Persist + probe
        try { _settings.Update(s => s.EwsAddresses[SelectedPrinter.DeviceId] = url); }
        catch (Exception ex) { _dialog.ShowError("Failed to save", ex); return; }
        StatusMessage = $"EWS URL set to {url}. Probing…";
        try
        {
            var ok = await _ews.ProbeAsync(url);
            StatusMessage = ok
                ? $"EWS reachable at {url}."
                : $"EWS URL saved but {url} is not reachable. The URL is still saved; click Refresh to retry.";
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "EWS probe failed");
            StatusMessage = $"EWS URL saved; probe failed: {ex.Message}";
        }
        // Force the Open EWS button's CanExecute to re-evaluate
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// v0.2.0: browses the local network for IPP-advertising printers (mDNS
    /// PTR query for _ipp._tcp.local and _printer._tcp.local). Results show
    /// in a separate list below the installed printers; the user can use the
    /// IPP URL to query supplies or push firmware.
    /// </summary>
    private async Task DiscoverNetworkPrintersAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Browsing the network for IPP-advertising printers (mDNS, ~3s)…";
            var results = await _discovery.BrowseAsync();
            Discovered.Clear();
            foreach (var d in results) Discovered.Add(d);
            StatusMessage = results.Count == 0
                ? "No network printers found via mDNS. Make sure your firewall allows UDP 5353 multicast."
                : $"Found {results.Count} network printer(s) via mDNS.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Network discovery failed");
            StatusMessage = "Network discovery failed.";
            _dialog.ShowError("Network discovery failed", ex);
        }
        finally { IsBusy = false; }
    }

    private PrintJob? _selectedJob;
    public PrintJob? SelectedJob
    {
        get => _selectedJob;
        set => SetField(ref _selectedJob, value);
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Loading printers…";
            var all = await _printers.GetAllAsync();
            var filtered = ShowOnlyHp ? all.Where(p => p.IsHp).ToList() : all.ToList();
            // Populate the model image URI + family on each printer
            foreach (var p in filtered)
            {
                p.ModelImageUri = _modelImages.GetImageUri(p);
                p.ModelFamily = _modelImages.GetFamily(p);
            }
            Printers.Clear();
            foreach (var p in filtered) Printers.Add(p);
            if (SelectedPrinter is null && Printers.Count > 0)
                SelectedPrinter = Printers[0];
            else
                await LoadJobsAsync();
            StatusMessage = $"Loaded {filtered.Count} printer(s) — {DateTime.Now:HH:mm:ss}";

            // Fire-and-forget: walk SNMP consumables for each network HP printer in parallel
            // and update each PrinterInfo's Consumables list as data arrives. Non-network
            // printers and unparseable IPs are skipped by the service. The list view binds
            // to PrinterInfo.Consumables and will refresh itself per-row.
            _ = LoadConsumablesInBackgroundAsync(filtered);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load printers");
            StatusMessage = "Error loading printers.";
            _dialog.ShowError("Failed to load printers", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task LoadJobsAsync()
    {
        Jobs.Clear();
        if (SelectedPrinter is null) return;
        try
        {
            var jobs = await _printers.GetJobsAsync(SelectedPrinter.Name);
            foreach (var j in jobs) Jobs.Add(j);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load jobs for {Printer}", SelectedPrinter.Name);
        }
    }

    /// <summary>
    /// For every HP printer, kick off a consumable query (SNMP for network, IPP for any
    /// device that exposes a reachable IPP endpoint, including WSD-USB / IPP-over-USB
    /// devices that advertise via mDNS). Each completion assigns the result to
    /// <see cref="PrinterInfo.Consumables"/>, which raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// so the row in the printers list re-renders with the new chips.
    /// </summary>
    private async Task LoadConsumablesInBackgroundAsync(IList<PrinterInfo> printers)
    {
        var targets = printers.Where(p => p.IsHp).ToList();
        if (targets.Count == 0) return;

        try
        {
            var tasks = targets.Select(p => QueryOneAsync(p));
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Background consumable load completed with at least one failure.");
        }
    }

    private async Task QueryOneAsync(PrinterInfo printer)
    {
        try
        {
            var items = await _consumables.GetConsumablesAsync(printer, CancellationToken.None);
            // Set even when empty so the UI clears any stale chip row from a previous
            // refresh on the same PrinterInfo instance.
            printer.Consumables = items;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Consumable fetch failed for {Printer}", printer.Name);
        }
    }

    private async Task OpenAdvancedAsync()
    {
        if (SelectedPrinter is null) return;
        var hwnd = HSA.Helpers.WindowHelper.GetMainWindowHandle();
        await _printers.OpenAdvancedPropertiesAsync(SelectedPrinter.Name, hwnd);
    }

    private async Task OpenPreferencesAsync()
    {
        if (SelectedPrinter is null) return;
        var hwnd = HSA.Helpers.WindowHelper.GetMainWindowHandle();
        await _printers.OpenPrintingPreferencesAsync(SelectedPrinter.Name, hwnd);
    }

    private async Task CancelJobAsync()
    {
        if (SelectedPrinter is null || SelectedJob is null) return;
        try
        {
            await HSA.Helpers.JobControlHelper.CancelJobAsync(SelectedPrinter.Name, SelectedJob.Id);
            await LoadJobsAsync();
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Cancel job failed", ex);
        }
    }

    private async Task PauseJobAsync()
    {
        if (SelectedPrinter is null || SelectedJob is null) return;
        try
        {
            await HSA.Helpers.JobControlHelper.PauseJobAsync(SelectedPrinter.Name, SelectedJob.Id);
            await LoadJobsAsync();
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Pause job failed", ex);
        }
    }

    private async Task ResumeJobAsync()
    {
        if (SelectedPrinter is null || SelectedJob is null) return;
        try
        {
            await HSA.Helpers.JobControlHelper.ResumeJobAsync(SelectedPrinter.Name, SelectedJob.Id);
            await LoadJobsAsync();
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Resume job failed", ex);
        }
    }

    private async Task DetectFirmwareAsync()
    {
        if (SelectedPrinter is null) return;
        IsBusy = true;
        try
        {
            StatusMessage = $"Detecting firmware for {SelectedPrinter.Name}…";
            var info = await _firmware.DetectAsync(SelectedPrinter, CancellationToken.None);
            StatusMessage = $"Firmware: {info.CurrentVersionDisplay} ({info.DetectionMethodDisplay})";
            _dialog.ShowInfo("Firmware",
                $"Printer: {info.PrinterName}\n" +
                $"Model: {info.ModelIdentifier ?? "(unknown)"}\n" +
                $"Current firmware: {info.CurrentVersionDisplay}\n" +
                $"Detection: {info.DetectionMethodDisplay}\n" +
                $"Update capability: {info.UpdateCapabilityDisplay}\n\n" +
                $"HP support: {info.HpSupportUrl}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Firmware detection failed");
            _dialog.ShowError("Firmware detection failed", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task InstallDriverForSelectedAsync()
    {
        if (SelectedPrinter is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select HP driver INF",
            Filter = "INF files (*.inf)|*.inf|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Installing driver INF (will prompt for admin)…";
            var result = await _drivers.InstallFromInfAsync(dlg.FileName);
            if (result.Success)
            {
                StatusMessage = "Driver installed. Rescanning devices…";
                _dialog.ShowInfo("Driver installed", "The driver was added to the driver store. A rescan was triggered.");
            }
            else
            {
                StatusMessage = "Driver install failed.";
                _dialog.ShowError("Driver install failed",
                    $"exit={result.ExitCode}\n\n{result.StdErr}\n\n{result.StdOut}".Trim());
            }
        }
        finally { IsBusy = false; }
    }
}
