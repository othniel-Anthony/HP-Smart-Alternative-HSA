using System.Collections.ObjectModel;
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
    private readonly IDialogService _dialog;
    private readonly ILogger<PrintersViewModel> _log;

    public ObservableCollection<PrinterInfo> Printers { get; } = new();
    public ObservableCollection<PrintJob> Jobs { get; } = new();

    private PrinterInfo? _selectedPrinter;
    public PrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set { if (SetField(ref _selectedPrinter, value)) { _ = LoadJobsAsync(); } }
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

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

    public PrintersViewModel(
        IPrinterService printers,
        IDriverService drivers,
        IFirmwareService firmware,
        IModelImageService modelImages,
        IDialogService dialog,
        ILogger<PrintersViewModel> log)
    {
        _printers = printers;
        _drivers = drivers;
        _firmware = firmware;
        _modelImages = modelImages;
        _dialog = dialog;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
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
            StatusMessage = $"Detecting firmware for {SelectedPrinter.Name}â€¦";
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
            StatusMessage = "Installing driver INF (will prompt for admin)â€¦";
            var result = await _drivers.InstallFromInfAsync(dlg.FileName);
            if (result.Success)
            {
                StatusMessage = "Driver installed. Rescanning devicesâ€¦";
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
