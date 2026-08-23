using System.Collections.ObjectModel;
using HSA.Models;
using HSA.Services;
using Microsoft.Extensions.Logging;

namespace HSA.ViewModels;

public sealed class FirmwareViewModel : ObservableObject
{
    private readonly IPrinterService _printers;
    private readonly IFirmwareService _firmware;
    private readonly IDialogService _dialog;
    private readonly ILogger<FirmwareViewModel> _log;

    public ObservableCollection<PrinterInfo> Printers { get; } = new();
    public ObservableCollection<FirmwareInfo> Results { get; } = new();

    private PrinterInfo? _selectedPrinter;
    public PrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetField(ref _selectedPrinter, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private FirmwareInfo? _selectedResult;
    public FirmwareInfo? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetField(ref _selectedResult, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private string _statusMessage = "Select a printer, then click Detect firmware.";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    public AsyncRelayCommand LoadPrintersCommand { get; }
    public AsyncRelayCommand DetectSelectedCommand { get; }
    public AsyncRelayCommand DetectAllHpCommand { get; }
    public RelayCommand OpenHpSupportCommand { get; }
    public AsyncRelayCommand PushFirmwareCommand { get; }

    public FirmwareViewModel(
        IPrinterService printers,
        IFirmwareService firmware,
        IDialogService dialog,
        ILogger<FirmwareViewModel> log)
    {
        _printers = printers;
        _firmware = firmware;
        _dialog = dialog;
        _log = log;

        LoadPrintersCommand = new AsyncRelayCommand(LoadPrintersAsync);
        DetectSelectedCommand = new AsyncRelayCommand(DetectSelectedAsync, () => SelectedPrinter is not null);
        DetectAllHpCommand = new AsyncRelayCommand(DetectAllHpAsync);
        OpenHpSupportCommand = new RelayCommand(_ => OpenSupportForSelected(),
            _ => SelectedResult is not null && !string.IsNullOrEmpty(SelectedResult.HpSupportUrl));
        PushFirmwareCommand = new AsyncRelayCommand(PushFirmwareAsync, () => SelectedPrinter is not null);
    }

    public async Task LoadPrintersAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = "Loading printers…";
            var all = await _printers.GetAllAsync();
            var hp = all.Where(p => p.IsHp).ToList();
            Printers.Clear();
            foreach (var p in hp) Printers.Add(p);
            StatusMessage = $"Loaded {Printers.Count} HP printer(s).";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load printers for firmware view");
            _dialog.ShowError("Failed to load printers", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task DetectSelectedAsync()
    {
        if (SelectedPrinter is null) return;
        await DetectOneAsync(SelectedPrinter);
    }

    private async Task DetectAllHpAsync()
    {
        if (Printers.Count == 0) await LoadPrintersAsync();
        IsBusy = true;
        try
        {
            int done = 0;
            foreach (var p in Printers)
            {
                ct.ThrowIfCancellationRequested();
                StatusMessage = $"Detecting firmware for {p.Name}…";
                await DetectOneAsync(p);
                done++;
            }
            StatusMessage = $"Detection complete ({done} printer(s)).";
        }
        finally { IsBusy = false; }
    }

    private CancellationToken ct => CancellationToken.None;

    private async Task DetectOneAsync(PrinterInfo p)
    {
        try
        {
            var info = await _firmware.DetectAsync(p);
            // Replace any prior row for the same printer
            for (int i = 0; i < Results.Count; i++)
            {
                if (string.Equals(Results[i].PrinterName, info.PrinterName, StringComparison.OrdinalIgnoreCase))
                {
                    Results.RemoveAt(i);
                    break;
                }
            }
            Results.Insert(0, info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Firmware detection failed for {Printer}", p.Name);
            _dialog.ShowError($"Firmware detection failed for {p.Name}", ex);
        }
    }

    private void OpenSupportForSelected()
    {
        if (SelectedResult is null || string.IsNullOrEmpty(SelectedResult.HpSupportUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SelectedResult.HpSupportUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Failed to open browser", ex);
        }
    }

    /// <summary>
    /// v0.2.0: pushes a firmware update to the selected printer via the PWG 5100.11
    /// IPP System Services Update-Operation. Asks the user for a URL, sends the
    /// request, and reports the IPP status code.
    /// </summary>
    private async Task PushFirmwareAsync()
    {
        if (SelectedPrinter is null) return;
        var p = SelectedPrinter;
        var url = Microsoft.VisualBasic.Interaction.InputBox(
            $"Enter the direct URL of the firmware file for '{p.Name}':\n\n" +
            "The printer will download the file and apply it. The process may take several minutes.",
            "Push firmware update",
            "");
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var firmwareUri))
        {
            _dialog.ShowError("Invalid URL", "Please enter an absolute URL (starting with http:// or https://).");
            return;
        }
        if (!firmwareUri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _dialog.ShowError("Unsupported URL scheme", "Use an http:// or https:// URL.");
            return;
        }

        if (!_dialog.ConfirmDestructive(
            "Push firmware update",
            $"Send PWG 5100.11 Update-Operation to '{p.Name}' for:\n  {firmwareUri}\n\n" +
            "The printer will download the file and apply it. The process may take several " +
            "minutes and the printer may reboot. Continue?",
            "Push"))
            return;

        IsBusy = true;
        try
        {
            StatusMessage = $"Sending PWG 5100.11 Update-Operation to {p.Name}…";
            var res = await _firmware.PushUpdateAsync(p, firmwareUri);
            StatusMessage = res.Message;
            _dialog.ShowInfo("Firmware update", res.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Firmware push failed");
            StatusMessage = "Firmware push failed.";
            _dialog.ShowError("Firmware push failed", ex);
        }
        finally { IsBusy = false; }
    }
}
