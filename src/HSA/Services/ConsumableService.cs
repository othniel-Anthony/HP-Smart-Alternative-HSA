using System.Net;
using System.Text.RegularExpressions;
using HSA.Models;
using HSA.Native;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

public sealed class ConsumableService : IConsumableService
{
    // The root of the prtMarkerSuppliesTable (RFC 3805). Walking this gives us every
    // column for every row in one round-trip. We then correlate by the trailing OID index.
    private static readonly ObjectIdentifier SuppliesTableRoot = new("1.3.6.1.2.1.43.11.1.1");

    // HP cartridges typically encode the part number in the description, e.g.
    //   "HP CF258A Black Toner Cartridge"      -> "CF258A"
    //   "HP 305A Black Original LaserJet Toner" -> "305A"
    //   "HP 63 Black Ink Cartridge"             -> "63"
    // We grab the first 2-4 char/digit token after "HP ".
    private static readonly Regex PartNumberRegex = new(
        @"\bHP\s+([A-Z0-9]{2,5})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Marker colorant class -> color name. RFC 3805 doesn't enforce names, so this is a best-effort
    // mapping. Real printers often store hex color codes; we also fall back to a simple heuristic
    // from the description text below.
    private static readonly Dictionary<int, string> ColorIndexMap = new()
    {
        [1]  = "other",
        [2]  = "black",
        [3]  = "cyan",
        [4]  = "magenta",
        [5]  = "yellow",
        [6]  = "red",
        [7]  = "green",
        [8]  = "blue",
        [9]  = "white"
    };

    private readonly SnmpClient _snmp;
    private readonly ILogger<ConsumableService> _log;

    public ConsumableService(ILogger<ConsumableService> log)
    {
        _log = log;
        _snmp = new SnmpClient();
    }

    public async Task<IReadOnlyList<ConsumableStatus>> GetConsumablesAsync(
        PrinterInfo printer, CancellationToken ct = default)
    {
        if (printer is null) return Array.Empty<ConsumableStatus>();
        if (!printer.IsNetworkPrinter || string.IsNullOrEmpty(printer.IpAddress))
            return Array.Empty<ConsumableStatus>();
        if (!IPAddress.TryParse(printer.IpAddress, out var ip))
            return Array.Empty<ConsumableStatus>();

        try
        {
            var rows = await _snmp.WalkTableAsync(ip, SuppliesTableRoot, ct);
            if (rows.Count == 0) return Array.Empty<ConsumableStatus>();

            // Optionally pull color names from prtMarkerColorantTable (best-effort)
            var colorantRows = await _snmp.WalkTableAsync(ip, new ObjectIdentifier("1.3.6.1.2.1.43.12.1.1"), ct);
            var colorantNames = new Dictionary<int, string>();
            foreach (var kv in colorantRows)
            {
                if (kv.Value.Count > 0)
                    colorantNames[kv.Key] = kv.Value[0].Value;
            }

            var list = new List<ConsumableStatus>(rows.Count);
            foreach (var kv in rows.OrderBy(k => k.Key))
            {
                var description = kv.Value
                    .FirstOrDefault(v => v.Column.ToString() == SnmpClient.MarkerSuppliesDescription.ToString())
                    .Value ?? "";
                var levelStr = kv.Value
                    .FirstOrDefault(v => v.Column.ToString() == SnmpClient.MarkerSuppliesLevel.ToString())
                    .Value;
                var maxStr = kv.Value
                    .FirstOrDefault(v => v.Column.ToString() == SnmpClient.MarkerSuppliesMaxCapacity.ToString())
                    .Value;
                var classStr = kv.Value
                    .FirstOrDefault(v => v.Column.ToString() == SnmpClient.MarkerSuppliesClass.ToString())
                    .Value;
                var colorIdxStr = kv.Value
                    .FirstOrDefault(v => v.Column.ToString() == SnmpClient.MarkerSuppliesColorIndex.ToString())
                    .Value;

                // Skip entries with no useful description
                if (string.IsNullOrWhiteSpace(description) &&
                    string.IsNullOrEmpty(levelStr) &&
                    string.IsNullOrEmpty(maxStr))
                    continue;

                var (max, level, percent) = ParseLevel(levelStr, maxStr);

                var klass = ConsumableClass.Other;
                if (int.TryParse(classStr, out var c) && c >= 1 && c <= 8) klass = (ConsumableClass)c;

                var color = ResolveColor(colorIdxStr, colorantNames, description);

                var partNumber = TryExtractPartNumber(description);

                list.Add(new ConsumableStatus
                {
                    PrinterName    = printer.Name,
                    Index          = kv.Key,
                    Description    = string.IsNullOrEmpty(description)
                                       ? $"Supply #{kv.Key}"
                                       : description,
                    PartNumber     = partNumber,
                    Color          = color,
                    Class          = klass,
                    LevelPercent   = percent,
                    MaxCapacity    = max,
                    CurrentLevel   = level,
                    Health         = ConsumableHealthRules.FromPercent(percent),
                    DetectedAt     = DateTime.UtcNow
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Consumable query failed for {Printer}", printer.Name);
            return Array.Empty<ConsumableStatus>();
        }
    }

    public async Task<IReadOnlyList<ConsumableStatus>> GetAllConsumablesAsync(
        IEnumerable<PrinterInfo> printers,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        var list = new List<ConsumableStatus>();
        var all = printers.ToList();
        var total = all.Count;
        var done = 0;
        foreach (var p in all)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, total, p.Name));
            var items = await GetConsumablesAsync(p, ct);
            list.AddRange(items);
            done++;
        }
        progress?.Report((total, total, string.Empty));
        return list;
    }

    private static (int? max, int? level, int? percent) ParseLevel(string? levelStr, string? maxStr)
    {
        // RFC 3805: prtMarkerSuppliesLevel can be -1/-2/-3 for "unknown" or 0..maxCapacity.
        // Some vendors send -1 for unknown. We treat any negative as "no reading".
        int? max = int.TryParse(maxStr, out var m) && m > 0 ? m : null;
        int? level = null;
        if (int.TryParse(levelStr, out var l) && l >= 0) level = l;

        if (level is null || max is null || max == 0) return (max, level, null);
        var pct = (int)Math.Round(100.0 * level.Value / max.Value);
        pct = Math.Clamp(pct, 0, 100);
        return (max, level, pct);
    }

    private static string ResolveColor(string? colorIdxStr,
        IReadOnlyDictionary<int, string> colorantNames, string description)
    {
        if (int.TryParse(colorIdxStr, out var idx) && ColorIndexMap.TryGetValue(idx, out var named))
        {
            if (colorantNames.TryGetValue(idx, out var specific) && !string.IsNullOrEmpty(specific))
                return specific;
            return named;
        }
        // Heuristic from description
        var d = description.ToLowerInvariant();
        if (d.Contains("black")) return "black";
        if (d.Contains("cyan"))  return "cyan";
        if (d.Contains("magenta")) return "magenta";
        if (d.Contains("yellow")) return "yellow";
        return "unknown";
    }

    private static string? TryExtractPartNumber(string description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var m = PartNumberRegex.Match(description);
        return m.Success ? m.Groups[1].Value : null;
    }
}
