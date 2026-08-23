using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using HSA.Models;
using HSA.Native;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Reads ink/toner consumable levels from HP printers using a multi-transport strategy:
///   1. SNMP (RFC 3805 prtMarkerSuppliesTable) for direct network HP printers.
///   2. IPP (PWG 5100.13 marker-* attributes) for any HP printer reachable via IPP,
///      including WSD-USB / IPP-over-USB devices whose endpoint is discoverable.
///   3. WSD-Print (Microsoft WSD-Print GetPrinterElements SOAP) for WSD-USB printers
///      and any other WSD device whose XAddr we can derive from the registry / WSDAPI.
///
/// Each transport is tried in order. As soon as one returns consumable data we stop;
/// if all return empty, the caller surfaces the "no data" state to the user.
/// </summary>
public sealed class ConsumableService : IConsumableService
{
    // The root of the prtMarkerSuppliesTable (RFC 3805). Walking this gives us every
    // column for every row in one round-trip. We then correlate by the trailing OID index.
    private static readonly ObjectIdentifier SuppliesTableRoot = new("1.3.6.1.2.1.43.11.1.1");

    private static readonly Regex PartNumberRegex = new(
        @"\bHP\s+([A-Z0-9]{2,5})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<int, string> ColorIndexMap = new()
    {
        [1]  = "other",  [2]  = "black", [3] = "cyan",  [4] = "magenta",
        [5]  = "yellow", [6]  = "red",   [7] = "green", [8] = "blue",  [9] = "white"
    };

    private readonly SnmpClient _snmp;
    private readonly IppConsumableSource _ipp;
    private readonly WsdPrintConsumableSource _wsd;
    private readonly EwsService _ews;
    private readonly EwsDiscoveryService _ewsDiscovery;
    private readonly PrinterEndpointDiscovery _endpoint;
    private readonly SettingsService _settings;
    private readonly ILogger<ConsumableService> _log;

    public ConsumableService(
        ILogger<ConsumableService> log,
        IppConsumableSource ipp,
        WsdPrintConsumableSource wsd,
        EwsService ews,
        EwsDiscoveryService ewsDiscovery,
        PrinterEndpointDiscovery endpoint,
        SettingsService settings)
    {
        _log = log;
        _snmp = new SnmpClient();
        _ipp = ipp;
        _wsd = wsd;
        _ews = ews;
        _ewsDiscovery = ewsDiscovery;
        _endpoint = endpoint;
        _settings = settings;
    }

    public async Task<IReadOnlyList<ConsumableStatus>> GetConsumablesAsync(
        PrinterInfo printer, CancellationToken ct = default)
    {
        if (printer is null) return Array.Empty<ConsumableStatus>();

        // 1) Try SNMP if we have an IP. Works for direct network HP printers.
        if (printer.IsNetworkPrinter
            && !string.IsNullOrEmpty(printer.IpAddress)
            && IPAddress.TryParse(printer.IpAddress, out var ip))
        {
            var snmpItems = await QuerySnmpAsync(printer, ip, ct);
            if (snmpItems.Count > 0) return snmpItems;
        }

        // 2) Try IPP via the endpoint discovery (Location, mDNS, etc.). Works for any
        //    HP printer that's reachable via IPP - including WSD-USB / IPP-over-USB devices
        //    that advertise via mDNS or whose Location URL is still valid.
        var ippUri = await _endpoint.FindIppEndpointAsync(printer, ct);
        if (!string.IsNullOrEmpty(ippUri))
        {
            var ippItems = await _ipp.GetConsumablesAsync(ippUri, printer.Name, ct);
            if (ippItems.Count > 0) return ippItems;
        }

        // 3) Try EWS (Embedded Web Server). The user can pin a printer's EWS URL in
        //    Settings (keyed by the spooler DeviceId); otherwise EwsDiscoveryService
        //    auto-derives it from the printer's IP address (network / Wi-Fi) or
        //    mDNS browse. EWS returns the same CMYK state as the EWS home page —
        //    matches what HP Smart shows.
        var ewsUrl = await ResolveEwsUrlAsync(printer, ct);
        if (!string.IsNullOrEmpty(ewsUrl))
        {
            var ewsItems = await _ews.GetConsumablesAsync(ewsUrl, printer.Name, ct);
            if (ewsItems is { Count: > 0 }) return ewsItems;
        }

        // 4) Try WSD-Print SOAP. The XAddr is derived from the WSD Port Monitor's port
        //    config (for WSD-USB devices) or from the printer's Location URL. The WSD Port
        //    Monitor forwards requests on the canonical http://<uuid>/PrintService URL to
        //    the device over WSD-over-USB. Network WSD printers expose the same URL on a
        //    reachable port.
        var wsdXAddr = _endpoint.FindWsdUsbXAddr(printer) ?? ippUri;
        if (!string.IsNullOrEmpty(wsdXAddr))
        {
            var wsdItems = await _wsd.GetConsumablesAsync(wsdXAddr, printer.Name, ct);
            if (wsdItems.Count > 0) return wsdItems;
        }

        return Array.Empty<ConsumableStatus>();
    }

    /// <summary>
    /// Resolves the EWS URL for the printer. Tries in order:
    ///   1. User-pinned URL keyed by DeviceId (always wins).
    ///   2. For network printers / printers with an IP — <c>http://&lt;ip&gt;/</c>.
    ///   3. mDNS browse by name / UUID.
    /// Returns null if no candidate is reachable.
    /// </summary>
    private async Task<string?> ResolveEwsUrlAsync(PrinterInfo printer, CancellationToken ct)
    {
        if (_ewsDiscovery is null)
        {
            _log.LogDebug("EWS lookup skipped: EwsDiscoveryService is null");
            return null;
        }
        if (string.IsNullOrEmpty(printer.DeviceId))
        {
            _log.LogDebug("EWS lookup skipped: printer.DeviceId is empty for '{Name}'", printer.Name);
            return null;
        }
        // Cheap: pinned is dictionary lookup; IP probe is one HTTP GET; mDNS
        // fallback is ~2s. Total <= ~3s worst case, usually < 50ms for pinned.
        return await _ewsDiscovery.DiscoverAsync(printer, ct);
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

    private async Task<IReadOnlyList<ConsumableStatus>> QuerySnmpAsync(
        PrinterInfo printer, IPAddress ip, CancellationToken ct)
    {
        try
        {
            var rows = await _snmp.WalkTableAsync(ip, SuppliesTableRoot, ct);
            if (rows.Count == 0) return Array.Empty<ConsumableStatus>();

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

                if (string.IsNullOrWhiteSpace(description)
                    && string.IsNullOrEmpty(levelStr)
                    && string.IsNullOrEmpty(maxStr))
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
                    Description    = string.IsNullOrEmpty(description) ? $"Supply #{kv.Key}" : description,
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
            _log.LogDebug(ex, "SNMP consumable query failed for {Printer}", printer.Name);
            return Array.Empty<ConsumableStatus>();
        }
    }

    private static (int? max, int? level, int? percent) ParseLevel(string? levelStr, string? maxStr)
    {
        int? max = int.TryParse(maxStr, out var m) && m > 0 ? m : null;
        int? level = null;
        if (int.TryParse(levelStr, out var l) && l >= 0) level = l;
        if (level is null || max is null || max == 0) return (max, level, null);
        var pct = (int)Math.Round(100.0 * level.Value / max.Value);
        return (max, level, Math.Clamp(pct, 0, 100));
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
