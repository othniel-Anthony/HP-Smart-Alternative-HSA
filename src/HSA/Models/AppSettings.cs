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
}
