using System.Text.Json.Serialization;

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
    [JsonPropertyName("ThemeMode")]
    public string? ThemeModeRaw { get; set; }

    /// <summary>Convenience accessor that defaults to "Light" when the raw value is null.</summary>
    [JsonIgnore]
    public string ThemeMode
    {
        get => string.IsNullOrEmpty(ThemeModeRaw) ? "Light" : ThemeModeRaw;
        set => ThemeModeRaw = value;
    }

    /// <summary>
    /// When true, the Printers list filter ("HP only") starts in this state on launch.
    /// Tolerates <c>null</c> on read (older / hand-edited settings.json files may have nulls);
    /// falls back to <c>true</c>.
    /// </summary>
    [JsonPropertyName("StartWithHpOnlyFilter")]
    public bool? StartWithHpOnlyFilterRaw { get; set; }

    /// <summary>Convenience accessor that defaults to true when the raw value is null.</summary>
    [JsonIgnore]
    public bool StartWithHpOnlyFilter
    {
        get => StartWithHpOnlyFilterRaw ?? true;
        set => StartWithHpOnlyFilterRaw = value;
    }

    /// <summary>
    /// Last URL pasted into the Drivers tab's "Quick install" field. Pre-fills the box
    /// on next launch so the user can re-run with one click. Tolerates null.
    /// </summary>
    [JsonPropertyName("LastQuickInstallUrl")]
    public string? LastQuickInstallUrlRaw { get; set; }

    /// <summary>Convenience accessor that defaults to empty string when the raw value is null.</summary>
    [JsonIgnore]
    public string LastQuickInstallUrl
    {
        get => LastQuickInstallUrlRaw ?? string.Empty;
        set => LastQuickInstallUrlRaw = value;
    }

    /// <summary>
    /// Per-printer EWS (Embedded Web Server) URL map. Keyed by the printer's
    /// DeviceId (the spooler's stable per-printer ID) so the EWS URL survives
    /// the printer's name or port name changing. Value is the base URL,
    /// e.g. "http://192.168.1.99" or "https://192.168.1.99".
    /// Tolerates null on read (returns an empty dictionary instead of failing deserialization).
    /// </summary>
    [JsonPropertyName("EwsAddresses")]
    public Dictionary<string, string> EwsAddressesRaw { get; set; } = new();

    /// <summary>Convenience accessor that defaults to an empty dictionary when the raw value is null.</summary>
    [JsonIgnore]
    public Dictionary<string, string> EwsAddresses
    {
        get => EwsAddressesRaw ?? new Dictionary<string, string>();
        set => EwsAddressesRaw = value ?? new Dictionary<string, string>();
    }
}
