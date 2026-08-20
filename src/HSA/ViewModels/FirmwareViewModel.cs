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
    public PrinterInfo? SelectedPrinter { get => _selectedPrinter; set => SetField(ref _selectedPrinter, value); }

    private FirmwareInfo? _selectedResult;
    public FirmwareInfo? SelectedResult { get => _selectedResult; set => SetField(ref _selectedResult, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private string _statusMessage = "Select a printer, then click Detect firmware.";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    public AsyncRelayCommand LoadPrintersCommand { get; }
    public AsyncRelayCommand DetectSelectedCommand { get; }
    public AsyncRelayCommand DetectAllHpCommand { get; }
    public RelayCommand OpenHpSupportCommand { get; }

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
}
