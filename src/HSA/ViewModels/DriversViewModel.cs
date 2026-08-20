using System.Collections.ObjectModel;
using HSA.Models;
using HSA.Services;
using Microsoft.Extensions.Logging;

namespace HSA.ViewModels;

public sealed class DriversViewModel : ObservableObject
{
    private readonly IDriverService _drivers;
    private readonly IDialogService _dialog;
    private readonly ILogger<DriversViewModel> _log;

    public ObservableCollection<DriverInfo> Drivers { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    private bool _hpOnly = true;
    public bool HpOnly { get => _hpOnly; set { if (SetField(ref _hpOnly, value)) _ = RefreshAsync(); } }

    private bool _onlyInUse;
    public bool OnlyInUse { get => _onlyInUse; set { if (SetField(ref _onlyInUse, value)) _ = RefreshAsync(); } }

    private DriverInfo? _selectedDriver;
    public DriverInfo? SelectedDriver { get => _selectedDriver; set => SetField(ref _selectedDriver, value); }

    private int _progressDone;
    public int ProgressDone { get => _progressDone; set { if (SetField(ref _progressDone, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    private int _progressTotal;
    public int ProgressTotal { get => _progressTotal; set { if (SetField(ref _progressTotal, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public int ProgressPercent => ProgressTotal == 0 ? 0 : (int)(100.0 * ProgressDone / ProgressTotal);
    public bool IsProgressing => ProgressTotal > 0 && ProgressDone < ProgressTotal;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RemoveSelectedCommand { get; }
    public AsyncRelayCommand RemoveAllHpCommand { get; }
    public AsyncRelayCommand InstallFromInfCommand { get; }

    public DriversViewModel(IDriverService drivers, IDialogService dialog, ILogger<DriversViewModel> log)
    {
        _drivers = drivers;
        _dialog = dialog;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedDriver is not null);
        RemoveAllHpCommand = new AsyncRelayCommand(RemoveAllHpAsync);
        InstallFromInfCommand = new AsyncRelayCommand(InstallFromInfAsync);
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Enumerating driver storeâ€¦";
            var all = await _drivers.GetAllAsync(hpOnly: HpOnly);
            if (OnlyInUse) all = all.Where(d => d.UsedByPrinters.Count > 0).ToList();
            Drivers.Clear();
            foreach (var d in all) Drivers.Add(d);
            StatusMessage = $"Loaded {Drivers.Count} driver package(s).";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to enumerate drivers");
            StatusMessage = "Error enumerating drivers.";
            _dialog.ShowError("Failed to enumerate drivers", ex);
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveSelectedAsync()
    {
        if (SelectedDriver is null) return;
        var d = SelectedDriver;
        var msg = d.UsedByPrinters.Count > 0
            ? $"Remove '{d.OriginalName}'?\n\nThis driver is currently used by:\n  - " +
              string.Join("\n  - ", d.UsedByPrinters) +
              "\n\nRemoving it will break those printers. Continue with force-remove?"
            : $"Remove '{d.OriginalName}' from the driver store?";

        if (!_dialog.ConfirmDestructive("Remove driver", msg, "Remove")) return;

        IsBusy = true;
        try
        {
            var res = await _drivers.RemoveAsync(d, force: d.UsedByPrinters.Count > 0);
            AppendLog($"[{(res.Success ? "OK" : "FAIL")}] {d.OriginalName} ({d.PublishedName}) exit={res.ExitCode}");
            if (!res.Success)
                _dialog.ShowError("Driver removal failed",
                    $"exit={res.ExitCode}\n\n{res.StdErr}".Trim());
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveAllHpAsync()
    {
        if (Drivers.Count == 0)
        {
            _dialog.ShowInfo("Nothing to remove", "No HP driver packages were found in the driver store.");
            return;
        }
        if (!_dialog.ConfirmDestructive(
            "Remove ALL HP drivers",
            $"This will remove {Drivers.Count} HP driver package(s) from the driver store. " +
            "Printers that use them will stop working. " +
            "You can reinstall them later from Windows Update or an INF.\n\nProceed?",
            "Remove all HP drivers")) return;

        IsBusy = true;
        ProgressTotal = Drivers.Count;
        ProgressDone = 0;
        try
        {
            var progress = new Progress<(int Done, int Total, string Current)>(p =>
            {
                ProgressDone = p.Done;
                ProgressTotal = p.Total;
                StatusMessage = $"Removing {p.Current} ({p.Done + 1}/{p.Total})â€¦";
            });
            var snapshot = Drivers.ToList();
            var results = await _drivers.RemoveAllHpAsync(progress);
            int ok = results.Count(r => r.Result.Success);
            int fail = results.Count - ok;
            foreach (var (driver, res) in results)
                AppendLog($"[{(res.Success ? "OK" : "FAIL")}] {driver.OriginalName} exit={res.ExitCode}");
            AppendLog($"Summary: {ok} removed, {fail} failed.");
            StatusMessage = $"Removed {ok} HP driver package(s); {fail} failed.";
            _dialog.ShowInfo("HP driver cleanup complete",
                $"{ok} driver package(s) removed.\n{fail} failed (see log panel for details).");
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            ProgressDone = ProgressTotal;
        }
    }

    private async Task InstallFromInfAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select HP driver INF",
            Filter = "INF files (*.inf)|*.inf|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Installing INF (admin UAC will appear)â€¦";
            var res = await _drivers.InstallFromInfAsync(dlg.FileName);
            AppendLog($"[{(res.Success ? "OK" : "FAIL")}] add-driver {dlg.FileName} exit={res.ExitCode}");
            if (!res.Success)
                _dialog.ShowError("Install failed", $"exit={res.ExitCode}\n{res.StdErr}");
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    private void AppendLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        Log.Insert(0, stamped);
        if (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }
}
