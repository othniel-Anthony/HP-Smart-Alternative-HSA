using System.Collections.ObjectModel;
using HSA.Models;
using HSA.Services;
using Microsoft.Extensions.Logging;

namespace HSA.ViewModels;

public sealed class SuppliesViewModel : ObservableObject
{
    private readonly IPrinterService _printers;
    private readonly IConsumableService _consumables;
    private readonly IDialogService _dialog;
    private readonly ILogger<SuppliesViewModel> _log;

    public ObservableCollection<ConsumableStatus> Items { get; } = new();
    public ObservableCollection<PrinterInfo> Printers { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private string _statusMessage = "Click Refresh to query your printers for ink/toner levels.";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    private int _progressDone;
    public int ProgressDone { get => _progressDone; set { if (SetField(ref _progressDone, value)) OnPropertyChanged(nameof(ProgressPercent)); } }

    private int _progressTotal;
    public int ProgressTotal { get => _progressTotal; set { if (SetField(ref _progressTotal, value)) OnPropertyChanged(nameof(ProgressPercent)); } }

    public int ProgressPercent => ProgressTotal == 0 ? 0 : (int)(100.0 * ProgressDone / ProgressTotal);

    private int _filterIndex; // 0 = All, 1 = Low, 2 = Replace
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (SetField(ref _filterIndex, value))
                ApplyFilter();
        }
    }

    public ObservableCollection<ConsumableStatus> FilteredItems { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand LoadPrintersCommand { get; }

    public SuppliesViewModel(
        IPrinterService printers,
        IConsumableService consumables,
        IDialogService dialog,
        ILogger<SuppliesViewModel> log)
    {
        _printers = printers;
        _consumables = consumables;
        _dialog = dialog;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadPrintersCommand = new RelayCommand(_ => _ = LoadPrintersAsync());
    }

    public async Task LoadPrintersAsync()
    {
        try
        {
            var all = await _printers.GetAllAsync();
            Printers.Clear();
            // Show every HP printer (network, local, USB/WSD) so the user can see
            // which ones have supplies and which don't.
            foreach (var p in all.Where(p => p.IsHp)) Printers.Add(p);
            StatusMessage = Printers.Count == 0
                ? "No HP printers found. Click Refresh to query for consumables."
                : $"{Printers.Count} HP printer(s) ready. WSD-USB devices may not return supplies — see the status column.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load printers for supplies view");
            StatusMessage = "Error loading printers.";
        }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Printers.Count == 0) await LoadPrintersAsync();
            StatusMessage = "Querying ink/toner levels…";
            Items.Clear();
            ProgressTotal = Printers.Count;
            ProgressDone = 0;

            var progress = new Progress<(int Done, int Total, string Current)>(p =>
            {
                ProgressDone = p.Done;
                ProgressTotal = p.Total;
                if (!string.IsNullOrEmpty(p.Current))
                    StatusMessage = $"Querying {p.Current} ({p.Done + 1}/{p.Total})…";
            });

            var all = await _consumables.GetAllConsumablesAsync(Printers, progress);

            // For printers that returned no consumables, add a placeholder "not available"
            // row so the user knows we tried and can see the reason.
            var printersWithSupplies = all.Select(c => c.PrinterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var noData = new List<ConsumableStatus>();
            foreach (var p in Printers)
            {
                if (printersWithSupplies.Contains(p.Name)) continue;
                noData.Add(new ConsumableStatus
                {
                    PrinterName  = p.Name,
                    Description  = IsWsdUsbOnly(p)
                        ? "Supplies unavailable (WSD-USB) — requires WSD-USB protocol support (v0.2+)"
                        : "No consumable data returned (printer may not support SNMP/IPP query)",
                    Color        = "unknown",
                    Class        = ConsumableClass.Other,
                    LevelPercent = null,
                    Health       = ConsumableHealth.Unknown,
                    DetectedAt   = DateTime.UtcNow
                });
            }

            foreach (var c in all.OrderByDescending(c => c.Health)) Items.Add(c);
            foreach (var c in noData.OrderBy(c => c.PrinterName)) Items.Add(c);
            // Refresh FilteredItems (the Supplies view binds to that, not Items).
            ApplyFilter();
            StatusMessage = all.Count == 0
                ? $"No consumable data found for any of the {Printers.Count} HP printer(s). WSD-USB support is on the v0.2 roadmap."
                : $"Found {all.Count} consumable(s) across {Printers.Count} HP printer(s).";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Supplies refresh failed");
            StatusMessage = "Error querying consumables.";
            _dialog.ShowError("Supplies refresh failed", ex);
        }
        finally
        {
            IsBusy = false;
            ProgressDone = ProgressTotal;
        }
    }

    /// <summary>
    /// A WSD-USB-only printer is one whose PortName starts with "WSD-" or "USB" and
    /// that has no network address. We use this to surface a clear "not supported yet"
    /// message for the user.
    /// </summary>
    private static bool IsWsdUsbOnly(PrinterInfo p)
    {
        var isUsb = p.PortName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase)
                  || p.PortName.StartsWith("USB", StringComparison.OrdinalIgnoreCase)
                  || p.Connection == PrinterConnectionKind.Local;
        return isUsb && string.IsNullOrEmpty(p.IpAddress);
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        IEnumerable<ConsumableStatus> source = Items;
        source = FilterIndex switch
        {
            1 => source.Where(c => c.Health is ConsumableHealth.Low
                                     or ConsumableHealth.ReplaceSoon
                                     or ConsumableHealth.ReplaceNow
                                     or ConsumableHealth.Empty),
            2 => source.Where(c => c.Health is ConsumableHealth.ReplaceSoon
                                     or ConsumableHealth.ReplaceNow
                                     or ConsumableHealth.Empty),
            _ => source
        };
        foreach (var c in source) FilteredItems.Add(c);
    }
}
