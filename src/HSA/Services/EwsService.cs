using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HSA.Models;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Reads consumable / supply data from an HP printer's Embedded Web Server
/// (EWS). EWS is the HTTP server embedded in every network-capable HP printer;
/// the host's URL pattern is <c>http://&lt;ip&gt;/DevMgmt/&lt;FileName&gt;</c>.
///
/// The XML schemas are documented under
/// https://&lt;ip&gt;/schemas/ (also linked from each document) and belong to the
/// HP LEDM namespace. We only parse <c>ConsumableConfigDyn.xml</c> for now
/// (cartridge level, state, type) and <c>ProductConfigDyn.xml</c> (model,
/// firmware, network UIs).
///
/// For WSD-USB printers where the canonical XAddr is unreachable, the EWS
/// can still work IF the printer is on the host's network (Wi-Fi, Ethernet,
/// or a virtual adapter the WSD Port Monitor bridges). The user can pin the
/// printer's IP via Settings (per-UUID) so we know where to query.
/// </summary>
public sealed class EwsService
{
    private readonly ILogger<EwsService> _log;
    private readonly HttpClient _http;

    public EwsService(ILogger<EwsService> log)
    {
        _log = log;
        _http = new HttpClient(new HttpClientHandler
        {
            // Self-signed certs on the printer are common - accept them.
            // (Modern HP printers with HTTPS EWS often use a self-signed cert.)
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "HSA/0.2.1 (compatible; HSA)");
        _http.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
    }

    /// <summary>
    /// Tries to fetch the printer's EWS root and returns true on any HTTP 2xx
    /// response. Used to validate an IP the user entered in Settings.
    /// </summary>
    public async Task<bool> ProbeAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync(NormalizeBaseUrl(baseUrl) + "/", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches the consumable state for the printer at <paramref name="baseUrl"/>.
    /// Returns null on network / parse / 4xx failure.
    /// </summary>
    public async Task<IReadOnlyList<ConsumableStatus>?> GetConsumablesAsync(
        string baseUrl, string printerName, CancellationToken ct = default)
    {
        try
        {
            var xml = await FetchXmlAsync(baseUrl, "/DevMgmt/ConsumableConfigDyn.xml", ct);
            if (xml is null)
            {
                _log.LogWarning("EWS consumables fetch returned null XML for {Url} (printer={Printer})",
                    baseUrl, printerName);
                return null;
            }
            var items = ParseConsumables(xml, printerName);
            _log.LogInformation("EWS consumables for {Printer} ({Url}): {Count} items",
                printerName, baseUrl, items.Count);
            return items;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EWS consumables fetch failed for {Url} (printer={Printer})",
                baseUrl, printerName);
            return null;
        }
    }

    /// <summary>
    /// Fetches the product config (model, firmware, network UIs, UUID) for the
    /// printer at <paramref name="baseUrl"/>. Returns null on failure.
    /// </summary>
    public async Task<EwsProductInfo?> GetProductInfoAsync(
        string baseUrl, CancellationToken ct = default)
    {
        try
        {
            var xml = await FetchXmlAsync(baseUrl, "/DevMgmt/ProductConfigDyn.xml", ct);
            if (xml is null) return null;
            return ParseProductInfo(xml, baseUrl);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "EWS product config fetch failed for {Url}", baseUrl);
            return null;
        }
    }

    /// <summary>Fetches a status summary (alerts) for the printer.</summary>
    public async Task<IReadOnlyList<string>?> GetAlertsAsync(
        string baseUrl, CancellationToken ct = default)
    {
        try
        {
            var xml = await FetchXmlAsync(baseUrl, "/DevMgmt/ProductStatusDyn.xml", ct);
            if (xml is null) return null;
            return ParseAlerts(xml);
        }
        catch
        {
            return null;
        }
    }

    private async Task<XDocument?> FetchXmlAsync(string baseUrl, string path, CancellationToken ct)
    {
        var url = NormalizeBaseUrl(baseUrl) + path;
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("EWS GET {Url} returned {Status}", url, (int)resp.StatusCode);
            return null;
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        // EWS always gzips. The HttpClient transparently handles
        // Content-Encoding: gzip if Accept-Encoding is set, but be defensive.
        var encoding = resp.Content.Headers.ContentEncoding;
        Stream s = new MemoryStream(bytes);
        if (encoding.Contains("gzip"))
            s = new GZipStream(s, CompressionMode.Decompress);
        else if (encoding.Contains("deflate"))
            s = new DeflateStream(s, CompressionMode.Decompress);
        using var sr = new StreamReader(s, Encoding.UTF8);
        var text = await sr.ReadToEndAsync(ct);
        try
        {
            return XDocument.Parse(text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EWS GET {Url} returned non-XML body ({Len} bytes): {Preview}",
                url, text.Length, text.Length > 200 ? text.Substring(0, 200) : text);
            return null;
        }
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return "http://" + url;
    }

    private static IReadOnlyList<ConsumableStatus> ParseConsumables(XDocument doc, string printerName)
    {
        var root = doc.Root;
        if (root is null) return Array.Empty<ConsumableStatus>();
        // The schema uses three prefixes in the same XML:
        //   - ccdyn (default): container elements
        //   - dd:            most leaf data elements
        //   - dd2:           a few leaf elements (e.g. ConsumableID)
        // We walk the tree using local names so we don't have to enumerate every
        // namespace; XName equality matches across prefixes for the same expanded
        // name, but child elements with different prefixes won't share a namespace.
        // Helper: find the first descendant whose local name matches, regardless
        // of namespace. The XName.LocalName comparison makes this easy.
        var list = new List<ConsumableStatus>();
        var nodes = doc.Descendants()
            .Where(x => x.Name.LocalName == "ConsumableInfo");
        foreach (var n in nodes)
        {
            XElement? Find(string local) =>
                n.Elements().FirstOrDefault(e => e.Name.LocalName == local);
            var labelCode = Find("ConsumableLabelCode")?.Value ?? string.Empty;
            var type = Find("ConsumableTypeEnum")?.Value ?? string.Empty;
            var station = Find("ConsumableStation")?.Value ?? string.Empty;
            // Level is reported in two ways: percentage (most common) or drop count.
            var levelStr = Find("ConsumablePercentageLevelRemaining")?.Value;
            int? levelPct = int.TryParse(levelStr, out var pct) ? pct : null;
            // Life-state sub-tree
            var life = n.Elements().FirstOrDefault(e => e.Name.LocalName == "ConsumableLifeState");
            var state = life?.Elements().FirstOrDefault(e => e.Name.LocalName == "ConsumableState")?.Value ?? "ok";
            var brand = life?.Elements().FirstOrDefault(e => e.Name.LocalName == "Brand")?.Value ?? string.Empty;
            // Map label code to our color names. HP uses:
            //   K (Black), C (Cyan), M (Magenta), Y (Yellow),
            //   CMY (combined tri-color cartridge on some models like this 4650),
            //   PHK (Photo Black), LC/LM (Light Cyan/Magenta).
            var color = MapLabelCodeToColor(labelCode);
            // If the cartridge is failed/expired/unknown, mark as "Replace now" so the
            // user sees the same red chip HP Smart would show. For "ok" we additionally
            // factor in the level percent (low/replace-soon/now thresholds).
            ConsumableHealth health;
            if (string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase))
            {
                health = levelPct switch
                {
                    int p when p < 5   => ConsumableHealth.ReplaceNow,
                    int p when p < 15  => ConsumableHealth.ReplaceSoon,
                    int p when p < 50  => ConsumableHealth.Low,
                    _                 => ConsumableHealth.Ok
                };
            }
            else
            {
                health = state.ToLowerInvariant() switch
                {
                    "low"     => ConsumableHealth.Low,
                    "out"     => ConsumableHealth.Empty,
                    "failed"  => ConsumableHealth.ReplaceNow,
                    "expired" => ConsumableHealth.ReplaceNow,
                    "missing" => ConsumableHealth.ReplaceNow,
                    "wrong"   => ConsumableHealth.ReplaceNow,
                    "unknown" => ConsumableHealth.Unknown,
                    _         => ConsumableHealth.Unknown
                };
            }
            // If no level is reported but state is ok, the cartridge is full.
            if (levelPct is null && state.Equals("ok", StringComparison.OrdinalIgnoreCase))
                levelPct = 100;
            list.Add(new ConsumableStatus
            {
                PrinterName = printerName,
                // Description uses the selectability number ("HP 63XL") when available.
                Description = n.Elements().FirstOrDefault(e => e.Name.LocalName == "ConsumableSelectibilityNumber")?.Value
                              ?? $"Cartridge {labelCode}".Trim(),
                Color = color,
                Class = type switch
                {
                    "inkCartridge" => ConsumableClass.Ink,
                    "tonerCartridge" => ConsumableClass.Toner,
                    _ => ConsumableClass.Other
                },
                LevelPercent = levelPct,
                Health = health,
                PartNumber = n.Elements().FirstOrDefault(e => e.Name.LocalName == "ConsumableSelectibilityNumber")?.Value
                            ?? n.Elements().FirstOrDefault(e => e.Name.LocalName == "ConsumableID")?.Value,
                DetectedAt = DateTime.UtcNow,
                HealthDisplayOverride = state.Equals("ok", StringComparison.OrdinalIgnoreCase)
                    ? null : state  // show "failed" / "expired" etc. as the status pill
            });
        }
        return list;
    }

    private static string MapLabelCodeToColor(string labelCode)
    {
        if (string.IsNullOrWhiteSpace(labelCode)) return "unknown";
        var code = labelCode.Trim().ToLowerInvariant();
        return code switch
        {
            "k" or "black" or "blk" => "black",
            "c" or "cyan" => "cyan",
            "m" or "magenta" => "magenta",
            "y" or "yellow" => "yellow",
            "cmy" => "cmy",  // tri-color cartridge
            "phk" or "photo" => "photo",
            "lc" or "lightcyan" => "cyan",
            "lm" or "lightmagenta" => "magenta",
            _ => code
        };
    }

    private static EwsProductInfo? ParseProductInfo(XDocument doc, string baseUrl)
    {
        var n = doc.Root;
        if (n is null) return null;
        var dd = n.GetNamespaceOfPrefix("dd") ?? XNamespace.None;
        var prd = n.GetNamespaceOfPrefix("prdcfgdyn") ?? dd;
        return new EwsProductInfo(
            MakeAndModel: doc.Descendants(dd + "MakeAndModel").FirstOrDefault()?.Value ?? string.Empty,
            ModelBase: doc.Descendants(dd + "MakeAndModelBase").FirstOrDefault()?.Value ?? string.Empty,
            ModelFamily: doc.Descendants(dd + "MakeAndModelFamily").FirstOrDefault()?.Value ?? string.Empty,
            ProductNumber: doc.Descendants(dd + "ProductNumber").FirstOrDefault()?.Value ?? string.Empty,
            SerialNumber: doc.Descendants(dd + "SerialNumber").FirstOrDefault()?.Value ?? string.Empty,
            Uuid: doc.Descendants(dd + "UUID").FirstOrDefault()?.Value ?? string.Empty,
            FirmwareRevision: doc.Descendants(dd + "Revision").FirstOrDefault()?.Value ?? string.Empty,
            FirmwareDate: doc.Descendants(dd + "Date").FirstOrDefault()?.Value ?? string.Empty,
            EwsBaseUrl: baseUrl
        );
    }

    private static IReadOnlyList<string> ParseAlerts(XDocument doc)
    {
        var n = doc.Root;
        if (n is null) return Array.Empty<string>();
        var dd = n.GetNamespaceOfPrefix("dd") ?? XNamespace.None;
        var ad = n.GetNamespaceOfPrefix("ad") ?? dd;
        var list = new List<string>();
        foreach (var alert in doc.Descendants(n.GetDefaultNamespace() + "Alert"))
        {
            var id = alert.Element(ad + "ProductStatusAlertID")?.Value;
            var sev = alert.Element(ad + "Severity")?.Value;
            if (string.IsNullOrEmpty(id)) continue;
            list.Add($"{sev}: {id}");
        }
        // Also include the top-level StatusCategory if present.
        var cat = doc.Descendants(n.GetDefaultNamespace() + "StatusCategory")
            .Select(x => x.Value).FirstOrDefault();
        if (!string.IsNullOrEmpty(cat) && !list.Any(l => l.Contains(cat)))
            list.Insert(0, cat);
        return list;
    }
}

/// <summary>Product / model / firmware summary from EWS ProductConfigDyn.</summary>
public sealed record EwsProductInfo(
    string MakeAndModel,
    string ModelBase,
    string ModelFamily,
    string ProductNumber,
    string SerialNumber,
    string Uuid,
    string FirmwareRevision,
    string FirmwareDate,
    string EwsBaseUrl);
