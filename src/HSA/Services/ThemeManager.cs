using System.Windows;
using HSA.Models;

namespace HSA.Services;

/// <summary>
/// Owns the active Material You theme dictionary in <see cref="Application.Resources"/>.
/// Subscribes to <see cref="SettingsService.Changed"/> and swaps the merged
/// dictionary when the user toggles between light and dark.
///
/// Both <c>AppTheme.xaml</c> and <c>AppTheme.Dark.xaml</c> define the same set of
/// resource keys (PrimaryBrush, BackgroundBrush, …) — views reference them via
/// <c>DynamicResource</c> only for selected items, but because the entire
/// dictionary is replaced rather than mutated, even <c>StaticResource</c> lookups
/// will refresh when the new dictionary is loaded.
/// </summary>
public sealed class ThemeManager
{
    private const string LightFile = "Themes/AppTheme.xaml";
    private const string DarkFile  = "Themes/AppTheme.Dark.xaml";

    private readonly SettingsService _settings;

    public ThemeManager(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, next) => Apply(next.ThemeMode);
    }

    /// <summary>
    /// Returns "Light" or "Dark" — the value currently in effect. Useful for the
    /// SettingsViewModel's initial checkbox state.
    /// </summary>
    public string CurrentMode => Normalize(_settings.Current.ThemeMode);

    /// <summary>
    /// Replace the active theme dictionary with the one matching <paramref name="mode"/>.
    /// Safe to call before <see cref="Application.MainWindow"/> is shown
    /// (it will simply install the dictionary into <see cref="Application.Resources"/>).
    /// </summary>
    public void Apply(string mode)
    {
        var normalized = Normalize(mode);
        var file = normalized == "Dark" ? DarkFile : LightFile;
        var source = new Uri(file, UriKind.Relative);

        var app = Application.Current;
        if (app is null) return;

        var resources = app.Resources;
        var merged = resources.MergedDictionaries;
        ResourceDictionary? existing = null;
        for (int i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.ToString();
            // Match both AppTheme.xaml and AppTheme.Dark.xaml (case-insensitive — WPF
            // normalizes the Source string on read).
            if (src is not null && src.Contains("AppTheme", StringComparison.OrdinalIgnoreCase))
            {
                existing = merged[i];
                break;
            }
        }

        var fresh = new ResourceDictionary { Source = source };
        if (existing is not null)
        {
            var idx = merged.IndexOf(existing);
            merged.RemoveAt(idx);
            merged.Insert(idx, fresh);
        }
        else
        {
            merged.Add(fresh);
        }
    }

    private static string Normalize(string? mode)
        => string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
}
