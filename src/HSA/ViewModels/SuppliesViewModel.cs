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
            foreach (var p in all.Where(p => p.IsHp && p.IsNetworkPrinter)) Printers.Add(p);
            StatusMessage = Printers.Count == 0
                ? "No network HP printers found. Click Refresh to query for consumables."
                : $"{Printers.Count} network HP printer(s) ready.";
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
            foreach (var c in all.OrderByDescending(c => c.Health)) Items.Add(c);
            StatusMessage = $"Found {Items.Count} consumable(s) across {Printers.Count} printer(s).";
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
