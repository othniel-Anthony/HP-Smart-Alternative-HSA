using System.Collections.ObjectModel;
using System.IO;
using HSA.Models;
using HSA.Services;
using Microsoft.Extensions.Logging;

namespace HSA.ViewModels;

public sealed class DriversViewModel : ObservableObject
{
    private readonly IDriverService _drivers;
    private readonly IDialogService _dialog;
    private readonly SettingsService _settings;
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
    public DriverInfo? SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (SetField(ref _selectedDriver, value))
            {
                // SelectedDriver gates the "Remove selected" command. WPF only requeries
                // commands on input events, so a programmatic selection (clicking a row in
                // the list view) wouldn't refresh the button's IsEnabled until the next
                // mouse-move. Force the requery here.
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private int _progressDone;
    public int ProgressDone { get => _progressDone; set { if (SetField(ref _progressDone, value)) OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(IsProgressing)); } }
    private int _progressTotal;
    public int ProgressTotal { get => _progressTotal; set { if (SetField(ref _progressTotal, value)) OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(IsProgressing)); } }
    public int ProgressPercent => ProgressTotal == 0 ? 0 : (int)(100.0 * ProgressDone / ProgressTotal);
    public bool IsProgressing => ProgressTotal > 0 && ProgressDone < ProgressTotal;

    // True while a long-running driver op (single install OR bulk remove) is in flight.
    // Decoupled from IsBusy so the user can still browse, switch tabs, etc.
    // We also keep the progress bar visible for a moment after completion so the
    // user sees the 100% mark.
    private bool _isInstalling;
    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (!SetField(ref _isInstalling, value)) return;
            OnPropertyChanged(nameof(IsProgressing));
        }
    }

    // pnputil /add-driver doesn't report progress, so the bar animates in
    // indeterminate mode while waiting for the UAC + install + rescan pipeline.
    private bool _installIsIndeterminate = true;
    public bool InstallIsIndeterminate
    {
        get => _installIsIndeterminate;
        private set => SetField(ref _installIsIndeterminate, value);
    }

    // When true, Remove Selected and Remove ALL HP also unregister the PnP
    // devices that use each driver package (via `pnputil /remove-device`).
    // This clears the related HKLM\...\Services\<svc> and HKLM\...\Enum\<inst>
    // entries - i.e. the registry footprint of the driver. Defaults to true
    // because most users want a real cleanup. Disable for safer / quicker
    // removal that leaves registry entries orphaned until next reboot.
    private bool _fullRegistryCleanup = true;
    public bool FullRegistryCleanup
    {
        get => _fullRegistryCleanup;
        set => SetField(ref _fullRegistryCleanup, value);
    }

    // ===== Quick install (paste a URL, one click downloads + installs) =====
    // v0.2.0: the v0.1.x search & download card was removed; the user pastes a
    // direct driver download URL and one click downloads + extracts INFs + runs
    // pnputil /add-driver. The URL is persisted to settings.json and pre-fills
    // on next launch.
    private string _quickInstallUrl = string.Empty;
    public string QuickInstallUrl
    {
        get => _quickInstallUrl;
        set
        {
            if (SetField(ref _quickInstallUrl, value ?? string.Empty))
            {
                // Persist so the next launch re-fills the box.
                try { _settings.Update(s => s.LastQuickInstallUrl = _quickInstallUrl); } catch { /* best-effort */ }
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool CanQuickInstall =>
        !string.IsNullOrWhiteSpace(_quickInstallUrl) && !IsDownloading && !IsInstalling;

    // Reused by QuickInstallAsync to show progress on the same progress bar the
    // v0.1.x download flow used. Not a separate "search/download" indicator anymore.
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetField(ref _isDownloading, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    private bool _isDownloading;
    public double DownloadPercent { get => _downloadPercent; set => SetField(ref _downloadPercent, value); }
    private double _downloadPercent;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RemoveSelectedCommand { get; }
    public AsyncRelayCommand RemoveAllHpCommand { get; }
    public AsyncRelayCommand InstallFromInfCommand { get; }
    public AsyncRelayCommand ReinstallSelectedCommand { get; }
    public AsyncRelayCommand QuickInstallCommand { get; }
    public RelayCommand OpenHpSupportCommand { get; }
    public RelayCommand OpenMicrosoftUpdateCatalogCommand { get; }

    public DriversViewModel(
        IDriverService drivers,
        IDialogService dialog,
        SettingsService settings,
        ILogger<DriversViewModel> log)
    {
        _drivers = drivers;
        _dialog = dialog;
        _settings = settings;
        _log = log;
        _quickInstallUrl = settings.Current.LastQuickInstallUrl ?? string.Empty;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedDriver is not null);
        RemoveAllHpCommand = new AsyncRelayCommand(RemoveAllHpAsync);
        InstallFromInfCommand = new AsyncRelayCommand(InstallFromInfAsync);
        ReinstallSelectedCommand = new AsyncRelayCommand(
            ReinstallSelectedAsync,
            () => SelectedDriver is not null && !string.IsNullOrEmpty(SelectedDriver.InfPath) && !IsInstalling);
        QuickInstallCommand = new AsyncRelayCommand(QuickInstallAsync, () => CanQuickInstall);
        // v0.2.0: these open the standard search pages without a query. The user
        // searches inside the browser, copies a download URL, and pastes it into
        // the Quick install URL field above.
        OpenHpSupportCommand = new RelayCommand(_ => OpenBrowser("https://support.hp.com/drivers"));
        OpenMicrosoftUpdateCatalogCommand = new RelayCommand(_ => OpenBrowser("https://catalog.update.microsoft.com/"));
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Enumerating driver store…";
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
              "\n\nRemoving it will break those printers. Continue with force-remove?" +
              (FullRegistryCleanup
                  ? "\n\nFull registry cleanup is ON - every PnP device bound to this " +
                    "driver will also be unregistered (HKLM\\...\\Services\\<svc> and " +
                    "HKLM\\...\\Enum\\<inst> entries will be cleared)."
                  : "")
            : $"Remove '{d.OriginalName}' from the driver store?" +
              (FullRegistryCleanup
                  ? "\n\nFull registry cleanup is ON - every PnP device bound to this " +
                    "driver will also be unregistered."
                  : "");

        if (!_dialog.ConfirmDestructive("Remove driver", msg, "Remove")) return;

        IsInstalling = true;
        InstallIsIndeterminate = !FullRegistryCleanup;  // determinate when we know counts
        try
        {
            if (FullRegistryCleanup)
            {
                // Look up the PnP devices for an accurate count to drive the bar
                var instanceIds = await _drivers.GetPnpInstanceIdsAsync(d);
                ProgressTotal = Math.Max(instanceIds.Count, 1) + 1; // devices + package
                ProgressDone = 0;

                var progress = new Progress<(int Done, int Total, string Current, string Phase)>(_ =>
                {
                    // We don't get per-step progress back from the cleanup pipeline, so
                    // the bar just animates in indeterminate mode for the duration.
                });

                var res = await _drivers.RemoveWithRegistryCleanupAsync(d);
                AppendLog($"[{(res.FullySucceeded ? "OK" : "PARTIAL")}] {d.OriginalName}: {res.Summary}");
                foreach (var dev in res.DeviceRemovals)
                    AppendLog($"    {(dev.Success ? "OK" : "FAIL")} device {dev.InstanceId} exit={dev.ExitCode}");
                if (!res.DriverPackageRemoved)
                {
                    AppendLog($"    FAIL package: {res.DriverPackageError}");
                    _dialog.ShowError("Driver package removal failed",
                        res.DriverPackageError ?? $"exit code reported by pnputil /delete-driver");
                }
                else if (!res.FullySucceeded)
                {
                    _dialog.ShowInfo("Driver partially cleaned",
                        $"{d.OriginalName}: {res.Summary}. See the activity log for per-device details.");
                }

                ProgressDone = ProgressTotal;
                await Task.Delay(400);
            }
            else
            {
                // Legacy path: just /delete-driver (no device unregistration, no registry cleanup).
                var res = await _drivers.RemoveAsync(d, force: d.UsedByPrinters.Count > 0);
                AppendLog($"[{(res.Success ? "OK" : "FAIL")}] {d.OriginalName} ({d.PublishedName}) exit={res.ExitCode}");
                if (!res.Success)
                    _dialog.ShowError("Driver removal failed",
                        $"exit={res.ExitCode}\n\n{res.StdErr}".Trim());
            }
            await RefreshAsync();
        }
        finally
        {
            IsInstalling = false;
            InstallIsIndeterminate = true;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
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
            $"This will remove {Drivers.Count} HP driver package(s)." +
            (FullRegistryCleanup
                ? "\n\nFULL REGISTRY CLEANUP:\n" +
                  "- Every PnP device bound to these drivers will be unregistered\n" +
                  "  (HKLM\\...\\Services\\<svc> and HKLM\\...\\Enum\\<inst> cleared).\n" +
                  "- The driver packages will then be removed from the store."
                : "\n\nDriver packages will be removed from the store only. " +
                  "Registry entries (Services/Enum) will be left orphaned until the " +
                  "next reboot.") +
            "\n\nPrinters that use them will stop working. " +
            "You can reinstall later from Windows Update or an INF.\n\nProceed?",
            "Remove all HP drivers")) return;

        IsInstalling = true;
        InstallIsIndeterminate = false;
        ProgressTotal = Drivers.Count;
        ProgressDone = 0;
        try
        {
            if (FullRegistryCleanup)
            {
                var progress = new Progress<(int Done, int Total, string Current, string Phase)>(p =>
                {
                    ProgressDone = p.Done;
                    ProgressTotal = p.Total;
                    StatusMessage = $"{p.Phase}: {p.Current} ({p.Done + 1}/{p.Total})…";
                });
                var results = await _drivers.RemoveAllHpWithRegistryCleanupAsync(progress);
                int fullyOk = results.Count(r => r.FullySucceeded);
                int partial = results.Count(r => !r.FullySucceeded && (r.DeviceRemovals.Count > 0 || r.DriverPackageRemoved));
                int fullyFailed = results.Count - fullyOk - partial;
                foreach (var r in results)
                {
                    AppendLog($"[{(r.FullySucceeded ? "OK" : partial > 0 ? "PARTIAL" : "FAIL")}] " +
                              $"{r.DriverOriginalName}: {r.Summary}");
                    foreach (var d in r.DeviceRemovals.Where(x => !x.Success))
                        AppendLog($"    FAIL device {d.InstanceId} exit={d.ExitCode}");
                }
                AppendLog($"Summary: {fullyOk} fully cleaned, {partial} partial, {fullyFailed} failed.");
                StatusMessage = $"{fullyOk} cleaned, {partial} partial, {fullyFailed} failed.";
                _dialog.ShowInfo("HP driver cleanup complete",
                    $"{fullyOk} fully cleaned, {partial} partial, {fullyFailed} failed. " +
                    "See the activity log for per-driver details.");
            }
            else
            {
                var progress = new Progress<(int Done, int Total, string Current)>(p =>
                {
                    ProgressDone = p.Done;
                    ProgressTotal = p.Total;
                    StatusMessage = $"Removing {p.Current} ({p.Done + 1}/{p.Total})…";
                });
                var results = await _drivers.RemoveAllHpAsync(progress);
                int ok = results.Count(r => r.Result.Success);
                int fail = results.Count - ok;
                foreach (var (driver, res) in results)
                    AppendLog($"[{(res.Success ? "OK" : "FAIL")}] {driver.OriginalName} exit={res.ExitCode}");
                AppendLog($"Summary: {ok} removed, {fail} failed.");
                StatusMessage = $"Removed {ok} HP driver package(s); {fail} failed.";
                _dialog.ShowInfo("HP driver cleanup complete",
                    $"{ok} driver package(s) removed.\n{fail} failed (see log panel for details).");
            }
            await RefreshAsync();
        }
        finally
        {
            // Land the bar at 100% briefly so the user sees the completion mark
            ProgressDone = ProgressTotal;
            await Task.Delay(400);
            IsInstalling = false;
            InstallIsIndeterminate = true;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    private async Task InstallFromInfAsync()
    {
        if (IsInstalling) return; // re-entrancy guard: only one install at a time
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select HP driver INF",
            Filter = "INF files (*.inf)|*.inf|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        // Long-running op: use IsInstalling (not IsBusy) so the user can still
        // navigate, switch tabs, etc. while the driver installs.
        IsInstalling = true;
        InstallIsIndeterminate = true;
        ProgressTotal = 100;
        ProgressDone = 0;
        try
        {
            StatusMessage = "Installing INF (admin UAC will appear)…";

            // Tiny delay so the indeterminate animation has a chance to render
            // before we await the UAC-prompt + pnputil pipeline.
            await Task.Delay(50);

            var res = await _drivers.InstallFromInfAsync(dlg.FileName);
            AppendLog($"[{(res.Success ? "OK" : "FAIL")}] add-driver {dlg.FileName} exit={res.ExitCode}");

            // Land the bar at 100% before the refresh kicks in.
            InstallIsIndeterminate = false;
            ProgressDone = 100;
            await Task.Delay(250);

            if (!res.Success)
                _dialog.ShowError("Install failed", $"exit={res.ExitCode}\n{res.StdErr}");

            // Refresh on the UI thread after the bar fades.
            await RefreshAsync();
            StatusMessage = res.Success
                ? "Driver installed successfully."
                : "Install failed — see log.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "InstallFromInf failed");
            AppendLog($"[FAIL] {ex.Message}");
            _dialog.ShowError("Install failed", ex);
        }
        finally
        {
            IsInstalling = false;
            InstallIsIndeterminate = true;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    private void AppendLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        Log.Insert(0, stamped);
        if (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }

    // ===== Quick install (URL -> download -> install first INF) =====
    private async Task QuickInstallAsync()
    {
        var url = QuickInstallUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (IsDownloading || IsInstalling) return;

        AppendLog($"[INFO] Quick install from {url}");

        // 1) Download
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadedDriver? downloaded = null;
        try
        {
            var progress = new Progress<(long Done, long Total, double Percent)>(p => DownloadPercent = p.Percent);
            // Auto-derive a filename from the URL (last path segment + extension).
            var pathSeg = url;
            try
            {
                var u = new Uri(url);
                pathSeg = u.Segments.Length > 0 ? u.Segments[^1] : "driver.zip";
                pathSeg = Uri.UnescapeDataString(pathSeg);
                if (string.IsNullOrWhiteSpace(pathSeg) || !pathSeg.Contains('.'))
                    pathSeg = "driver-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            }
            catch { pathSeg = "driver-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"; }

            downloaded = await _drivers.DownloadFromUrlAsync(url, pathSeg, progress, CancellationToken.None);
            AppendLog($"[OK] downloaded {downloaded.FilePath} ({downloaded.SizeBytes:N0} bytes)");
            if (downloaded.ContainedInfs.Count > 0)
                AppendLog($"      contains {downloaded.ContainedInfs.Count} INF file(s) - installing first");
            else if (downloaded.IsContainer)
                AppendLog($"      no .inf files found in container - install will not run");
            else
                AppendLog($"      direct file - INF install needs an .inf, not a {Path.GetExtension(downloaded.FilePath)}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Quick install download failed");
            AppendLog($"[FAIL] download: {ex.Message}");
            _dialog.ShowError("Download failed", ex);
            return;
        }
        finally { IsDownloading = false; }

        // 2) Install first INF (if any)
        if (downloaded is null || downloaded.ContainedInfs.Count == 0)
        {
            StatusMessage = "Download complete, but no INFs found. Open the Downloads folder to inspect manually.";
            return;
        }
        var firstInf = downloaded.ContainedInfs[0];
        if (downloaded.ContainedInfs.Count > 1
            && !_dialog.ConfirmDestructive(
                "Multiple INFs found",
                $"{downloaded.ContainedInfs.Count} INF files were found in the package:\n\n" +
                string.Join("\n", downloaded.ContainedInfs.Select(p => "  - " + Path.GetFileName(p))) +
                $"\n\nInstall '{Path.GetFileName(firstInf)}' now? You can install the rest manually from " +
                "the Downloads folder if needed.",
                "Install first INF"))
        {
            StatusMessage = "Downloaded, but install cancelled.";
            return;
        }

        IsInstalling = true;
        InstallIsIndeterminate = true;
        try
        {
            StatusMessage = $"Installing {Path.GetFileName(firstInf)} (admin UAC will appear)…";
            var res = await _drivers.InstallFromInfAsync(firstInf, CancellationToken.None);
            AppendLog($"[{(res.Success ? "OK" : "FAIL")}] add-driver {firstInf} exit={res.ExitCode}");
            StatusMessage = res.Success
                ? $"Installed {Path.GetFileName(firstInf)}"
                : $"Install failed (exit {res.ExitCode}): {res.StdErr}";
            if (!res.Success)
                _dialog.ShowError("Install failed", $"exit={res.ExitCode}\n{res.StdErr}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Quick install install step failed");
            AppendLog($"[FAIL] install: {ex.Message}");
            _dialog.ShowError("Install failed", ex);
        }
        finally
        {
            IsInstalling = false;
            InstallIsIndeterminate = true;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // best-effort: swallow - we already logged earlier
        }
    }

    // ===== Reinstall selected driver (re-add the selected driver's INF to the store) =====
    private async Task ReinstallSelectedAsync()
    {
        if (SelectedDriver is null) return;
        var d = SelectedDriver;
        if (string.IsNullOrEmpty(d.InfPath) || !File.Exists(d.InfPath))
        {
            _dialog.ShowError("Cannot reinstall",
                $"No on-disk INF for '{d.OriginalName}'. The driver may have been removed from the store already. " +
                "Open HP support or MS catalog to find a fresh download, paste the URL into the Quick install field above.");
            return;
        }

        if (!_dialog.ConfirmDestructive(
            "Reinstall driver",
            $"Re-add '{d.OriginalName}' to the driver store from:\n  {d.InfPath}\n\n" +
            "This runs pnputil /add-driver /install. Admin UAC will appear. Continue?",
            "Reinstall"))
            return;

        IsInstalling = true;
        InstallIsIndeterminate = true;
        try
        {
            StatusMessage = $"Reinstalling {d.OriginalName} (admin UAC will appear)…";
            var res = await _drivers.InstallFromInfAsync(d.InfPath, CancellationToken.None);
            AppendLog($"[{(res.Success ? "OK" : "FAIL")}] reinstall {d.InfPath} exit={res.ExitCode}");
            StatusMessage = res.Success
                ? $"Reinstalled {d.OriginalName}"
                : $"Reinstall failed (exit {res.ExitCode}): {res.StdErr}";
            if (!res.Success)
                _dialog.ShowError("Reinstall failed", $"exit={res.ExitCode}\n{res.StdErr}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reinstall failed");
            AppendLog($"[FAIL] reinstall: {ex.Message}");
            _dialog.ShowError("Reinstall failed", ex);
        }
        finally
        {
            IsInstalling = false;
            InstallIsIndeterminate = true;
        }
    }
}
