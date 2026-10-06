using PrintHub.Core.Updates;

namespace PrintHub.App.Services;

public enum UpdateState { Idle, Checking, Available, Downloading, Ready, Failed }

/// <summary>
/// Looks for a newer HSA on GitHub shortly after start-up, downloads it in the background (when allowed) and restarts into it when the user says so.
/// The window shows <see cref="State"/> in a bar; the Settings page can ask for a check at any time.
/// </summary>
public sealed class UpdateManager
{
    public UpdateState State { get; private set; }
    public UpdateInfo? Info { get; private set; }
    public double Progress { get; private set; }
    /// <summary>The result of the last check or download, in words (used by the Settings page).</summary>
    public string Message { get; private set; } = "";
    string? _readyPath;
    bool _busy;

    public event Action? Changed;
    void Set(UpdateState s, string? message = null) { State = s; if (message is not null) Message = message; Changed?.Invoke(); }

    public static Version Current => Version.TryParse(AppState.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>
    /// Off while a developer or a UI test runs HSA with its own data folder (so a test build never replaces itself),
    /// unless the update address is overridden on purpose; and off when HSA is not running as its own exe.
    /// </summary>
    public static bool Available =>
        (Environment.GetEnvironmentVariable("HSA_DATA_DIR") is not { Length: > 0 } || Environment.GetEnvironmentVariable(UpdateService.OverrideEnvVar) is { Length: > 0 })
        && Environment.ProcessPath is { } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    public bool CanReplaceThisInstall => UpdateApplier.CanReplace(Environment.ProcessPath);

    /// <summary>Called a few seconds after the window opens.</summary>
    public async Task CheckOnLaunchAsync()
    {
        if (!Available) return;
        for (int i = 0; i < 50 && !App.State.SettingsLoaded; i++) await Task.Delay(200);
        var s = App.State.Settings;
        if (!s.CheckForUpdates) return;
        // twice a day is plenty; a deliberately overridden address (testing) is always asked
        if (s.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(12) && Environment.GetEnvironmentVariable(UpdateService.OverrideEnvVar) is not { Length: > 0 }) return;
        await CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true; Set(UpdateState.Checking, "Checking for updates…");
        try
        {
            using var http = UpdateService.CreateClient(AppState.Version);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var info = await UpdateService.CheckAsync(http, Current, cts.Token);
            App.State.Settings.LastUpdateCheck = DateTime.UtcNow; App.State.SaveSettings();

            if (info is null) { Info = null; Set(UpdateState.Idle, $"HSA {AppState.Version} is the latest version."); return; }
            Info = info;
            if (!manual && info.Version.ToString(3) == App.State.Settings.SkippedVersion) { Set(UpdateState.Idle, $"Version {info.Version.ToString(3)} is available (skipped)."); return; }
            Set(UpdateState.Available, $"Version {info.Version.ToString(3)} is available.");
            AppLog.Write($"Update available: {info.Tag}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Update check failed: " + ex.Message);
            Set(UpdateState.Idle, manual ? "Could not check for updates: " + ex.Message : Message);   // a failed background check stays quiet
            return;
        }
        finally { _busy = false; }

        if (State == UpdateState.Available && App.State.Settings.AutoDownloadUpdates && CanReplaceThisInstall) await DownloadAsync();
    }

    public async Task DownloadAsync()
    {
        if (Info is null || _busy) return;
        _busy = true; Progress = 0; Set(UpdateState.Downloading, $"Downloading version {Info.Version.ToString(3)}…");
        // a download can stall (no data for 30 s); it is then tried again, up to three times, before the bar offers "Try again"
        Exception? last = null;
        try
        {
            using var http = UpdateService.CreateClient(AppState.Version);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Progress = 0;
                    var progress = new Progress<double>(p => { Progress = p; Changed?.Invoke(); });
                    _readyPath = await UpdateService.DownloadAsync(http, Info, progress);
                    AppLog.Write($"Update downloaded and verified: {_readyPath}");
                    Set(UpdateState.Ready, $"Version {Info.Version.ToString(3)} is ready to install.");
                    return;
                }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException or IOException)
                {
                    last = ex; AppLog.Write($"Update download attempt {attempt} failed: {ex.Message}");
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }
        catch (Exception ex) { last = ex; AppLog.Write("Update download failed: " + ex.Message); }
        finally { _busy = false; }
        Set(UpdateState.Failed, "The update could not be downloaded: " + last?.Message);
    }

    /// <summary>Starts the verified update and closes HSA; it comes back in the new version. Returns false (after opening the release page) when this copy cannot be replaced.</summary>
    public bool RestartAndUpdate()
    {
        if (State != UpdateState.Ready || _readyPath is null || Info is null) return false;
        if (!CanReplaceThisInstall || Environment.ProcessPath is not { } target) { OpenReleasePage(); return false; }
        App.State.SaveSettings();
        UpdateApplier.StartApply(_readyPath, target);
        Microsoft.UI.Xaml.Application.Current.Exit();
        return true;
    }

    public void SkipThisVersion()
    {
        if (Info is null) return;
        App.State.Settings.SkippedVersion = Info.Version.ToString(3); App.State.SaveSettings();
        Set(UpdateState.Idle, $"Version {Info.Version.ToString(3)} skipped.");
    }

    public void Dismiss() => Set(UpdateState.Idle);

    public void OpenReleasePage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((Info?.PageUrl ?? new Uri($"https://github.com/{UpdateService.Repo}/releases/latest")).ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write("Could not open the release page: " + ex.Message); }
    }
}
