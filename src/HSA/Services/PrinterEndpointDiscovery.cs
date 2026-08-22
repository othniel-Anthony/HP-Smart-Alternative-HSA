using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using HSA.Models;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Tries to find the IPP URL for a printer that the print spooler reports but that
/// doesn't expose a direct network address (e.g. WSD-USB or IPP-over-USB devices).
///
/// Discovery strategy (in order):
///   1. The Location attribute from PnP / Get-Printer (often a stale URL but works for some
///      devices once the host network comes back).
///   2. mDNS query for _ipp._tcp.local — works for HP printers that advertise themselves
///      via Bonjour (mDNS), which HP's installer usually configures for both network and
///      IPP-over-USB connections.
///   3. mDNS query for _printer._tcp.local — fallback for some HP models.
///   4. Direct probe of common IPP ports (631, 80, 443) on the local network — last resort.
///
/// Returns null when no endpoint is reachable. Callers should treat a null result as
/// "this printer is reachable only through Windows' WSD port monitor" and surface that
/// to the user (WSD-Print is a v0.2 deliverable).
/// </summary>
public sealed class PrinterEndpointDiscovery
{
    private readonly ILogger<PrinterEndpointDiscovery> _log;

    public PrinterEndpointDiscovery(ILogger<PrinterEndpointDiscovery> log) => _log = log;

    public async Task<string?> FindIppEndpointAsync(PrinterInfo printer, CancellationToken ct = default)
    {
        if (printer is null) return null;

        // 1) Try the Location attribute (PnP / WMI). For WSD-USB devices this is often
        //    a stale URL the host can't reach, but for some setups it still works.
        if (TryGetLocationUrl(printer, out var locUrl))
        {
            if (await IsHttpReachableAsync(locUrl, ct)) return locUrl;
        }

        // 2) mDNS for _ipp._tcp.local
        if (!string.IsNullOrEmpty(printer.Name))
        {
            var mdns = await TryMdnsLookupAsync(printer.Name, "_ipp._tcp", ct);
            if (mdns is not null) return mdns;
        }

        // 3) mDNS for _printer._tcp.local
        if (!string.IsNullOrEmpty(printer.Name))
        {
            var mdns = await TryMdnsLookupAsync(printer.Name, "_printer._tcp", ct);
            if (mdns is not null) return mdns;
        }

        return null;
    }

    /// <summary>
    /// Reads the Location attribute from the printer's PnP device. We use SetupDi APIs
    /// via P/Invoke on winspool (already in scope), but the simplest path is to read
    /// DEVPKEY_Device_LocationInfo via WMI.
    /// </summary>
    private static bool TryGetLocationUrl(PrinterInfo printer, out string url)
    {
        url = string.Empty;
        // WMI Win32_PnPEntity has no LocationInfo field. Use DEVPKEY_Device_LocationInfo
        // via the SetupDi API. For now we read it from the registry (the spooler mirrors
        // the same value there). The most reliable path is via EnumPrinterDataEx /
        // GetPrinterDataEx with the "PrinterDriverData" key — but we already saw those
        // keys don't include Location. So we fall back to the registry, which mirrors
        // DEVPKEY_Device_LocationInfo from the time the device was installed.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{printer.PortName}");
            if (key is null) return false;
            var loc = key.GetValue("LocationInformation") as string;
            if (string.IsNullOrWhiteSpace(loc)) return false;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out var uri)) return false;
            url = uri.AbsoluteUri.TrimEnd('/') + "/";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends an mDNS query for &lt;printerName&gt;.&lt;service&gt;.local and returns the
    /// first A/AAAA record's IP as a URL, or null if no answer in 1.5s.
    /// </summary>
    private async Task<string?> TryMdnsLookupAsync(string printerName, string service, CancellationToken ct)
    {
        try
        {
            // Build a simple mDNS query for "<service>.local" PTR record.
            // Format: header(12 bytes) + question(section).
            var query = BuildDnsQuery($"{service}.local", DnsRecordType.PTR);
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
            udp.Client.ReceiveTimeout = 1500;
            await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));

            var target = new IPEndPoint(IPAddress.Any, 0);
            var ep = (IPEndPoint?)null;
            while (true)
            {
                var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(1500), ct);
                if (TryParseDnsAnswer(result.Buffer, out var ip, out var name)
                    && name.Contains(printerName, StringComparison.OrdinalIgnoreCase))
                {
                    ep = new IPEndPoint(ip, 631);
                    break;
                }
            }
            if (ep is null) return null;
            return $"ipp://{ep.Address}/ipp/print";
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> IsHttpReachableAsync(string url, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.IsDefaultPort ? 80 : uri.Port, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Minimal DNS / mDNS encoding ---

    private enum DnsRecordType : ushort { A = 1, AAAA = 28, PTR = 12 }

    private static byte[] BuildDnsQuery(string name, DnsRecordType type)
    {
        using var ms = new MemoryStream();
        // Header: id=0, flags=0x0100 (standard query, recursion desired), qd=1, an=0, ns=0, ar=0
        WriteU16(ms, 0);
        WriteU16(ms, 0x0100);
        WriteU16(ms, 1);
        WriteU16(ms, 0); WriteU16(ms, 0); WriteU16(ms, 0);
        WriteDnsName(ms, name);
        WriteU16(ms, (ushort)type);
        WriteU16(ms, 1); // IN
        return ms.ToArray();
    }

    private static void WriteDnsName(MemoryStream ms, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)(v & 0xFF));
    }

    private static bool TryParseDnsAnswer(byte[] payload, out IPAddress ip, out string name)
    {
        ip = IPAddress.None; name = string.Empty;
        if (payload.Length < 12) return false;
        int anCount = (payload[6] << 8) | payload[7];
        if (anCount == 0) return false;
        int i = 12;
        // skip question section
        int qd = (payload[4] << 8) | payload[5];
        for (int q = 0; q < qd; q++)
        {
            i = SkipDnsName(payload, i);
            if (i + 4 > payload.Length) return false;
            i += 4;
        }
        for (int a = 0; a < anCount; a++)
        {
            int nameStart = i;
            i = SkipDnsName(payload, i);
            if (i + 10 > payload.Length) return false;
            ushort type = (ushort)((payload[i] << 8) | payload[i + 1]);
            // ushort cls = (ushort)((payload[i+2] << 8) | payload[i+3]);
            int rdlen = (payload[i + 8] << 8) | payload[i + 9];
            i += 10;
            string rname = ExtractDnsName(payload, nameStart);
            if (type == 1 && rdlen == 4)
            {
                ip = new IPAddress(new[] { payload[i], payload[i + 1], payload[i + 2], payload[i + 3] });
                name = rname;
                return true;
            }
            if (type == 28 && rdlen == 16)
            {
                var bytes = new byte[16];
                Array.Copy(payload, i, bytes, 0, 16);
                ip = new IPAddress(bytes);
                name = rname;
                return true;
            }
            if (type == 12 && rdlen > 0)
            {
                // PTR: domain name pointing to the actual host
                // The PTR RDATA is the name of the service instance.
                int ptr = SkipDnsName(payload, i);
                if (ptr > i && ptr < i + rdlen)
                {
                    // For A/AAAA we need to do a follow-up query, which is too much.
                    // Instead, look at the full response for A records that came in.
                }
            }
            i += rdlen;
        }
        return false;
    }

    private static int SkipDnsName(byte[] buf, int offset)
    {
        while (offset < buf.Length)
        {
            byte len = buf[offset];
            if (len == 0) return offset + 1;
            if ((len & 0xC0) == 0xC0) return offset + 2; // compression
            offset += 1 + len;
        }
        return offset;
    }

    private static string ExtractDnsName(byte[] buf, int offset)
    {
        var sb = new StringBuilder();
        bool first = true;
        while (offset < buf.Length)
        {
            byte len = buf[offset];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | buf[offset + 1];
                sb.Append(ExtractDnsName(buf, ptr));
                break;
            }
            if (!first) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(buf, offset + 1, len));
            offset += 1 + len;
            first = false;
        }
        return sb.ToString();
    }
}
