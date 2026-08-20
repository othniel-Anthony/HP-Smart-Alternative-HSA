using System.Reflection;
using System.Windows.Input;
using HSA.Services;
using Microsoft.Extensions.Logging;

namespace HSA.ViewModels;

/// <summary>
/// Backs the Settings tab. Owns the dark-mode toggle (round-trips through
/// <see cref="SettingsService"/>), exposes the running app version, and provides
/// commands to open the user data folder and the GitHub repo.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialog;
    private readonly ILogger<SettingsViewModel> _log;

    public SettingsViewModel(SettingsService settings, IDialogService dialog, ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _dialog = dialog;
        _log = log;

        _isDarkMode = _settings.Current.ThemeMode.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        _startWithHpOnlyFilter = _settings.Current.StartWithHpOnlyFilter;

        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        OpenRepoCommand      = new RelayCommand(_ => OpenUrl("https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA"));
        OpenReleasesCommand  = new RelayCommand(_ => OpenUrl("https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases"));
        CopyVersionCommand   = new RelayCommand(_ =>
        {
            try { System.Windows.Clipboard.SetText(VersionDisplay); } catch { /* clipboard can fail under RDP, ignore */ }
        });
    }

    // ---- Theme ----

    private bool _isDarkMode;
    /// <summary>When true, the app uses the Material You dark theme.</summary>
    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (!SetField(ref _isDarkMode, value)) return;
            _settings.Update(s => s.ThemeMode = value ? "Dark" : "Light");
        }
    }

    // ---- Other persisted toggles ----

    private bool _startWithHpOnlyFilter;
    public bool StartWithHpOnlyFilter
    {
        get => _startWithHpOnlyFilter;
        set
        {
            if (!SetField(ref _startWithHpOnlyFilter, value)) return;
            _settings.Update(s => s.StartWithHpOnlyFilter = value);
        }
    }

    // ---- Version + metadata ----

    public string AppName        => "HSA";
    public string VersionDisplay => ReadInformationalVersion();
    public string BuildDate      => ReadBuildDate();
    public string TargetFramework => "Windows · .NET 8 · WPF";
    public string Publisher      => "Circuit & Ink";
    public string RepoUrl        => "https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA";

    /// <summary>Human-readable one-liner shown at the top of the About card.</summary>
    public string Tagline =>
        "A no-nonsense Windows utility for managing HP printers, drivers, and firmware — without HP Smart.";

    public string Description =>
        "HSA talks directly to the Windows print spooler, WMI, and (for network printers) SNMP / IPP. " +
        "No cloud account, no telemetry, no bundled browser. Just a single, self-contained .exe that does the job.";

    public string LogsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HSA", "Logs");

    public string DataPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HSA");

    public string SettingsFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HSA", "settings.json");

    // ---- Commands ----

    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenRepoCommand       { get; }
    public ICommand OpenReleasesCommand   { get; }
    public ICommand CopyVersionCommand    { get; }

    private void OpenDataFolder()
    {
        try
        {
            var dir = DataPath;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open data folder");
            _dialog.ShowError("Failed to open data folder", ex);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { /* user can copy from the textbox if the browser fails to launch */ }
    }

    private static string ReadInformationalVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // .NET 8 SDK appends "+<git-sha>" to InformationalVersion when building from a
            // git repo. The SHA is noise for the UI; strip everything from "+" onwards.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static string ReadBuildDate()
    {
        // For a self-contained single-file publish, Assembly.Location is empty — the
        // assembly is extracted to a temp dir. Use AppContext.BaseDirectory (which is
        // the user's real install folder for both regular and single-file builds) and
        // look for HSA.exe there.
        try
        {
            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "HSA.exe");
            if (System.IO.File.Exists(exe))
                return System.IO.File.GetLastWriteTime(exe).ToString("yyyy-MM-dd");
        }
        catch { /* fall through */ }
        return string.Empty;
    }
}
