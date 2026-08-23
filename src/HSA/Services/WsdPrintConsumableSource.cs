using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HSA.Models;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Queries ink/toner consumable levels from a WSD printer over HTTP using the
/// Microsoft WSD-Print "GetPrinterElements" SOAP operation, then parses the
/// wprt:Ink / wprt:InkLevel / wprt:InkName / wprt:MarkerHighLevel elements.
///
/// This is the standard way to read consumables from any WSD-capable printer
/// (network WSD and WSD-over-USB devices that expose an HTTP-reachable XAddr).
///
/// Reference:
///   http://schemas.microsoft.com/windows/2011/08/printing/wsprint
///   PWG 5100.13 - IPP attribute set for consumables
/// </summary>
public sealed class WsdPrintConsumableSource
{
    private static readonly Regex PartNumberRegex = new(
        @"\bHP\s+([A-Z0-9]{2,5})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<WsdPrintConsumableSource> _log;

    public WsdPrintConsumableSource(ILogger<WsdPrintConsumableSource> log)
    {
        _log = log;
    }

    /// <summary>
    /// Sends a WSD-Print GetPrinterElements SOAP request to <paramref name="xAddr"/>
    /// and parses the response into consumable statuses.
    /// </summary>
    /// <param name="xAddr">A URL like "http://192.168.1.50:5357/wsd/print" or the
    /// GUID-based URL the WSD port monitor stores in the registry.</param>
    /// <param name="printerName">Display name used in the returned <see cref="ConsumableStatus.PrinterName"/>.</param>
    public async Task<IReadOnlyList<ConsumableStatus>> GetConsumablesAsync(
        string xAddr, string printerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xAddr)) return Array.Empty<ConsumableStatus>();
        if (!Uri.TryCreate(xAddr, UriKind.Absolute, out var uri)) return Array.Empty<ConsumableStatus>();
        if (uri.Scheme != "http" && uri.Scheme != "https") return Array.Empty<ConsumableStatus>();

        try
        {
            var soap = BuildGetPrinterElementsSoap();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.IsDefaultPort ? 80 : uri.Port, ct);
            tcp.SendTimeout = (int)DefaultTimeout.TotalMilliseconds;
            tcp.ReceiveTimeout = (int)DefaultTimeout.TotalMilliseconds;
            await using var stream = tcp.GetStream();

            var request =
                $"POST {uri.PathAndQuery} HTTP/1.1\r\n" +
                $"Host: {uri.Host}\r\n" +
                "Content-Type: application/soap+xml; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(soap)}\r\n" +
                "Expect:\r\n" +
                "Connection: close\r\n\r\n" + soap;
            var bytes = Encoding.UTF8.GetBytes(request);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);

            // Read response
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int read;
            var headerBytes = new MemoryStream();
            int contentLength = -1;
            var headerDone = false;
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                if (!headerDone)
                {
                    headerBytes.Write(buf, 0, read);
                    var headerData = headerBytes.ToArray();
                    var sep = Encoding.ASCII.GetBytes("\r\n\r\n");
                    var idx = IndexOf(headerData, sep);
                    if (idx >= 0)
                    {
                        var headerText = Encoding.ASCII.GetString(headerData, 0, idx);
                        foreach (var line in headerText.Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                _ = int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                        headerDone = true;
                        var leftover = headerData.Length - (idx + 4);
                        if (leftover > 0) ms.Write(headerData, idx + 4, leftover);
                        if (contentLength >= 0 && ms.Length >= contentLength) break;
                    }
                }
                else
                {
                    ms.Write(buf, 0, read);
                    if (contentLength >= 0 && ms.Length >= contentLength) break;
                }
            }
            if (ms.Length == 0) return Array.Empty<ConsumableStatus>();
            var responseXml = Encoding.UTF8.GetString(ms.ToArray());
            return ParseResponse(responseXml, printerName);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WSD-Print GetPrinterElements failed for {XAddr}", xAddr);
            return Array.Empty<ConsumableStatus>();
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static string BuildGetPrinterElementsSoap()
    {
        var messageId = $"urn:uuid:{Guid.NewGuid()}";
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:wprt=""http://schemas.microsoft.com/windows/2011/08/printing/wsprint"">
  <s:Header>
    <wsa:To>{WebUtility.HtmlEncode("")}</wsa:To>
    <wsa:Action>http://schemas.microsoft.com/windows/2011/08/printing/wsprint/GetPrinterElements</wsa:Action>
    <wsa:MessageID>{messageId}</wsa:MessageID>
    <wsa:ReplyTo>
      <wsa:Address>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:Address>
    </wsa:ReplyTo>
  </s:Header>
  <s:Body>
    <wprt:GetPrinterElements>
      <wprt:PrinterElements/>
    </wprt:GetPrinterElements>
  </s:Body>
</s:Envelope>";
    }

    /// <summary>
    /// Parses the WSD-Print GetPrinterElements response. Per the spec, consumable
    /// data is in the &lt;wprt:Ink&gt; element, with one or more per-cartridge
    /// entries. Each entry has wprt:InkLevel, wprt:InkName, wprt:MarkerHighLevel
    /// keyed by wprt:Ink/@r:row.
    /// </summary>
    private static IReadOnlyList<ConsumableStatus> ParseResponse(string xml, string printerName)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            // WSD-Print uses namespace wprt. The row attribute is in the standard
            // WS-Addressing "r" namespace (http://schemas.xmlsoap.org/ws/2004/08/addressing).
            XNamespace wprt = "http://schemas.microsoft.com/windows/2011/08/printing/wsprint";
            XNamespace r = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            // Find the printer's response — there may be multiple but we want the one
            // matching our request. Without resolving the full WS-Addressing reply
            // chain, we just take the first Ink element we find.
            var ink = doc.Descendants(wprt + "Ink").FirstOrDefault();
            if (ink is null) return Array.Empty<ConsumableStatus>();

            // wprt:Ink may have @r:row attribute like "1" and child elements
            // wprt:InkLevel, wprt:InkName, wprt:Color, wprt:MarkerHighLevel, etc.
            // In WSD the structure is:
            //   <wprt:Ink r:row="1">
            //     <wprt:InkLevel>87</wprt:InkLevel>
            //     <wprt:InkName>HP 67 Black</wprt:InkName>
            //     <wprt:MarkerHighLevel>100</wprt:MarkerHighLevel>
            //     ...
            //   </wprt:Ink>
            // OR a single attribute structure where each element is a child of
            // <wprt:PrinterElements>.

            var inkes = doc.Descendants(wprt + "Ink").ToList();
            if (inkes.Count == 0) return Array.Empty<ConsumableStatus>();

            var list = new List<ConsumableStatus>();
            int idx = 0;
            foreach (var oneInk in inkes)
            {
                idx++;
                var levelStr = oneInk.Element(wprt + "InkLevel")?.Value
                              ?? oneInk.Element(wprt + "MarkerLevel")?.Value;
                var highStr  = oneInk.Element(wprt + "MarkerHighLevel")?.Value;
                var name     = oneInk.Element(wprt + "InkName")?.Value
                              ?? oneInk.Element(wprt + "MarkerName")?.Value
                              ?? $"Supply #{idx}";
                var color    = oneInk.Element(wprt + "Color")?.Value
                              ?? oneInk.Element(wprt + "MarkerColor")?.Value
                              ?? "unknown";
                var type     = oneInk.Element(wprt + "Type")?.Value
                              ?? oneInk.Element(wprt + "MarkerType")?.Value
                              ?? "ink";

                if (!int.TryParse(levelStr, out var level) || level < 0) continue;
                int? high = int.TryParse(highStr, out var h) && h > 0 ? h : null;

                int? percent = level;
                if (high is int mh && mh > 0 && level > mh)
                {
                    percent = (int)Math.Round(100.0 * level / mh);
                    percent = Math.Clamp(percent.Value, 0, 100);
                }

                list.Add(new ConsumableStatus
                {
                    PrinterName  = printerName,
                    Index        = idx,
                    Description  = name,
                    PartNumber   = TryExtractPartNumber(name),
                    Color        = NormalizeColor(color),
                    Class        = MapType(type),
                    LevelPercent = percent,
                    MaxCapacity  = high,
                    CurrentLevel = level,
                    Health       = ConsumableHealthRules.FromPercent(percent),
                    DetectedAt   = DateTime.UtcNow
                });
            }
            return list;
        }
        catch (Exception)
        {
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
            "toner" or "toner-cartridge" => ConsumableClass.Toner,
            "ink" or "ink-cartridge" => ConsumableClass.InkCartridge,
            "fuser" => ConsumableClass.Fuser,
            "oil" => ConsumableClass.Oil,
            "wax" => ConsumableClass.Wax,
            "ink-ribbon" => ConsumableClass.InkRibbon,
            _ => ConsumableClass.Other
        };
    }

    private static string? TryExtractPartNumber(string description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var m = PartNumberRegex.Match(description);
        return m.Success ? m.Groups[1].Value : null;
    }
}
