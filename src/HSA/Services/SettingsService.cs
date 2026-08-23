using System.IO;
using System.Text;
using System.Text.Json;
using HSA.Models;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in
/// <c>%LOCALAPPDATA%\HSA\settings.json</c>. Raises <see cref="Changed"/> after a
/// successful in-process mutation so subscribers (e.g. <see cref="ThemeManager"/>)
/// can react without polling.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HSA");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // No BOM, no escaping of common chars (keeps the file diff-friendly).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<SettingsService>? _log;
    private readonly object _gate = new();

    /// <summary>The current in-memory settings. Always non-null.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Raised after <see cref="Save"/> writes a new snapshot to disk.</summary>
    public event EventHandler<AppSettings>? Changed;

    public SettingsService(ILogger<SettingsService>? log = null)
    {
        _log = log;
        Current = LoadFromDisk();
    }

    private static AppSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            // Use UTF-8 without BOM; explicit encoding avoids PowerShell 5.1 ANSI
            // decoding on read.
            var json = File.ReadAllText(SettingsPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            return s ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults rather than crashing.
            return new AppSettings();
        }
    }

    /// <summary>Persist the supplied snapshot. Triggers <see cref="Changed"/> on success.</summary>
    public void Save(AppSettings next)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(next, JsonOpts);
                File.WriteAllText(SettingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Current = next;
                Changed?.Invoke(this, next);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Failed to save settings to {Path}", SettingsPath);
                throw;
            }
        }
    }

    /// <summary>Convenience helper for single-property updates.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        var clone = new AppSettings
        {
            ThemeMode = Current.ThemeMode,
            StartWithHpOnlyFilter = Current.StartWithHpOnlyFilter,
            LastQuickInstallUrl = Current.LastQuickInstallUrl,
            EwsAddresses = new Dictionary<string, string>(Current.EwsAddresses)
        };
        mutate(clone);
        Save(clone);
    }
}
