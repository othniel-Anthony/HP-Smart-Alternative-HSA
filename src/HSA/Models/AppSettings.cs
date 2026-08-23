namespace HSA.Models;

/// <summary>
/// User-tunable settings persisted between sessions.
/// Serialized to <c>%LOCALAPPDATA%\HSA\settings.json</c> by <see cref="Services.SettingsService"/>.
/// Keep this record small and additive — old fields are tolerated on read.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// "Light" (default) or "Dark". Any other value falls back to "Light".
    /// </summary>
    public string ThemeMode { get; set; } = "Light";

    /// <summary>
    /// When true, the Printers list filter ("HP only") starts in this state on launch.
    /// </summary>
    public bool StartWithHpOnlyFilter { get; set; } = true;

    /// <summary>
    /// Last URL pasted into the Drivers tab's "Quick install" field. Pre-fills the box
    /// on next launch so the user can re-run with one click.
    /// </summary>
    public string LastQuickInstallUrl { get; set; } = string.Empty;

    /// <summary>
    /// Per-printer EWS (Embedded Web Server) URL map. Keyed by the printer's
    /// DeviceId (the spooler's stable per-printer ID) so the EWS URL survives
    /// the printer's name or port name changing. Value is the base URL,
    /// e.g. "http://192.168.1.99" or "https://192.168.1.99".
    /// </summary>
    public Dictionary<string, string> EwsAddresses { get; set; } = new();
}
