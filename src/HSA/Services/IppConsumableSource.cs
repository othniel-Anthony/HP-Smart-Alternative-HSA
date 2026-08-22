using System.Net;
using System.Text.RegularExpressions;
using HSA.Models;
using HSA.Native;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Reads ink/toner consumable levels from an HP printer over IPP, using the marker-*
/// attributes defined by PWG 5100.13 / RFC 8011.
///
/// Attributes we request:
///   - marker-names        (1setOf name(255))     human-friendly marker name
///   - marker-levels       (1setOf integer(0..100))  current level percentage
///   - marker-high-levels  (1setOf integer(0..100))  full-level threshold
///   - marker-low-levels   (1setOf integer(0..100))  low-level threshold
///   - marker-colors       (1setOf name(255))     "black", "cyan", etc.
///   - marker-types        (1setOf type2 keyword) "toner", "ink", "waste-ink", etc.
/// </summary>
public sealed class IppConsumableSource
{
    private static readonly string[] ConsumableAttributeNames =
    {
        "marker-names", "marker-levels", "marker-high-levels",
        "marker-low-levels", "marker-colors", "marker-types"
    };

    private static readonly Regex PartNumberRegex = new(
        @"\bHP\s+([A-Z0-9]{2,5})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IppClient _ipp;
    private readonly ILogger<IppConsumableSource> _log;

    public IppConsumableSource(ILogger<IppConsumableSource> log)
    {
        _log = log;
        _ipp = new IppClient(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Queries a printer at <paramref name="ippUri"/> and returns its consumables.
    /// Returns an empty list on any failure (network, parse, or no marker data).
    /// </summary>
    public async Task<IReadOnlyList<ConsumableStatus>> GetConsumablesAsync(
        string ippUri, string printerName, CancellationToken ct = default)
    {
        try
        {
            var set = await _ipp.GetPrinterAttributesAsync(ippUri, ConsumableAttributeNames, ct);
            if (set is null) return Array.Empty<ConsumableStatus>();

            var levels = set.GetInts("marker-levels");
            var names  = set.GetStrings("marker-names");
            var colors = set.GetStrings("marker-colors");
            var highs  = set.GetInts("marker-high-levels");
            var types  = set.GetStrings("marker-types");

            if (levels.Count == 0) return Array.Empty<ConsumableStatus>();

            var list = new List<ConsumableStatus>(levels.Count);
            for (int i = 0; i < levels.Count; i++)
            {
                int? level = levels[i];
                // -1 / -2 / -3 mean "unknown" in IPP.
                if (level is null || level < 0) continue;
                int? max = i < highs.Count ? highs[i] : null;
                string name = i < names.Count && !string.IsNullOrEmpty(names[i]) ? names[i] : $"Supply #{i + 1}";
                string color = i < colors.Count ? colors[i] : "unknown";
                string type = i < types.Count ? types[i] : "other";
                int? percent = level;
                if (max is int m && m > 0 && level > m)
                {
                    // If level > max, treat as raw count
                    percent = (int)Math.Round(100.0 * (level.Value) / m);
                    percent = Math.Clamp(percent.Value, 0, 100);
                }
                list.Add(new ConsumableStatus
                {
                    PrinterName  = printerName,
                    Index        = i + 1,
                    Description  = name,
                    PartNumber   = TryExtractPartNumber(name),
                    Color        = NormalizeColor(color),
                    Class        = MapType(type),
                    LevelPercent = percent,
                    MaxCapacity  = max,
                    CurrentLevel = level,
                    Health       = ConsumableHealthRules.FromPercent(percent),
                    DetectedAt   = DateTime.UtcNow
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPP consumable query failed for {Uri}", ippUri);
            return Array.Empty<ConsumableStatus>();
        }
    }

    private static string NormalizeColor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var c = raw.Trim().ToLowerInvariant();
        return c switch
        {
            "black" or "k" or "photo-black" or "matte-black" => "black",
            "cyan" or "c" => "cyan",
            "magenta" or "m" => "magenta",
            "yellow" or "y" => "yellow",
            "red" or "green" or "blue" or "white" => c,
            "gray" or "grey" or "photo-gray" => "gray",
            "light-cyan" or "lightcyan" => "lightcyan",
            "light-magenta" or "lightmagenta" => "lightmagenta",
            _ => c
        };
    }

    private static ConsumableClass MapType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return ConsumableClass.Other;
        return type.Trim().ToLowerInvariant() switch
        {
            "toner"      => ConsumableClass.Toner,
            "ink"        => ConsumableClass.Ink,
            "ink-cartridge" => ConsumableClass.InkCartridge,
            "fuser"      => ConsumableClass.Fuser,
            "oil"        => ConsumableClass.Oil,
            "wax"        => ConsumableClass.Wax,
            "ink-ribbon" => ConsumableClass.InkRibbon,
            _            => ConsumableClass.Other
        };
    }

    private static string? TryExtractPartNumber(string description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var m = PartNumberRegex.Match(description);
        return m.Success ? m.Groups[1].Value : null;
    }
}
