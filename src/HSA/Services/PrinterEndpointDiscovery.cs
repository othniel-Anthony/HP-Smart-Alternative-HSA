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
    /// Discovers all network printers advertising IPP or _printer._tcp via mDNS.
    /// Returns a list of <see cref="DiscoveredNetworkPrinter"/> with name, IP, port
    /// and IPP URL. Callers can offer these to the user to add to the spooler.
    /// Browses both _ipp._tcp.local and _printer._tcp.local and dedupes by IP+port.
    /// Browses 1.5s per service; total ~3s in the worst case.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredNetworkPrinter>> BrowseAsync(
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var perServiceTimeout = timeout ?? TimeSpan.FromMilliseconds(1500);
        var all = new Dictionary<(string ip, int port), DiscoveredNetworkPrinter>();
        foreach (var svc in new[] { "_ipp._tcp", "_printer._tcp" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var results = await BrowseServiceAsync(svc, perServiceTimeout, ct);
                foreach (var r in results)
                {
                    var key = (r.IpAddress, r.Port);
                    all[key] = r;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "mDNS browse for {Svc} failed", svc);
            }
        }
        return all.Values.OrderBy(p => p.Name).ToList();
    }

    private async Task<IReadOnlyList<DiscoveredNetworkPrinter>> BrowseServiceAsync(
        string service, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var query = BuildDnsQuery($"{service}.local", DnsRecordType.PTR);
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));

            var results = new List<DiscoveredNetworkPrinter>();
            var seenInstanceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow + timeout;
            // Accumulate responses; parse each as it arrives.
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                try
                {
                    var result = await udp.ReceiveAsync().WaitAsync(remaining, ct);
                    TryParseBrowseResponse(result.Buffer, results, seenInstanceNames);
                }
                catch (TimeoutException) { break; }
                catch (OperationCanceledException) { break; }
            }
            return results;
        }
        catch
        {
            return Array.Empty<DiscoveredNetworkPrinter>();
        }
    }

    private static void TryParseBrowseResponse(
        byte[] payload, List<DiscoveredNetworkPrinter> sink, HashSet<string> seenInstances)
    {
        if (payload.Length < 12) return;
        int anCount = (payload[6] << 8) | payload[7];
        int qd = (payload[4] << 8) | payload[5];
        int i = 12;
        // Skip question section
        for (int q = 0; q < qd; q++)
        {
            i = SkipDnsName(payload, i);
            if (i + 4 > payload.Length) return;
            i += 4;
        }
        // v0.2.12: also collect TXT records per instance name so we can
        // surface uuid / serial / adminurl to the EWS matcher.
        var txtByInstance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Walk answers + additionals. We extract:
        //  - PTR records (gives us the service instance name)
        //  - SRV records (gives us host + port for the instance)
        //  - A/AAAA records (gives us the IP for the host)
        //  - TXT records (gives us uuid / serial / adminurl for matching)
        // Real mDNS requires a follow-up query to resolve additional records; for
        // a single-packet browse we'll opportunistically link any A/AAAA records
        // we see in the same packet. This is good enough for the typical case
        // where the device's response includes its own address records.
        var ptrNames = new List<string>();
        var srvByInstance = new Dictionary<string, (string host, int port)>(StringComparer.OrdinalIgnoreCase);
        var aByHost = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        for (int a = 0; a < anCount; a++)
        {
            int nameStart = i;
            i = SkipDnsName(payload, i);
            if (i + 10 > payload.Length) return;
            ushort type = (ushort)((payload[i] << 8) | payload[i + 1]);
            int rdlen = (payload[i + 8] << 8) | payload[i + 9];
            i += 10;
            string rname = ExtractDnsName(payload, nameStart);
            if (type == 12 && rdlen > 0) // PTR
            {
                string target = ExtractDnsName(payload, i);
                ptrNames.Add(target);
            }
            else if (type == 33 && rdlen > 0) // SRV
            {
                // SRV: priority(2) weight(2) port(2) target
                int port = (payload[i + 4] << 8) | payload[i + 5];
                string target = ExtractDnsName(payload, i + 6);
                srvByInstance[StripTrailingDot(rname)] = (StripTrailingDot(target), port);
            }
            else if (type == 1 && rdlen == 4) // A
            {
                var ip = new IPAddress(new[] { payload[i], payload[i + 1], payload[i + 2], payload[i + 3] });
                aByHost[StripTrailingDot(rname)] = ip;
            }
            else if (type == 28 && rdlen == 16) // AAAA
            {
                var bytes = new byte[16];
                Array.Copy(payload, i, bytes, 0, 16);
                aByHost[StripTrailingDot(rname)] = new IPAddress(bytes);
            }
            else if (type == 16 && rdlen > 0) // TXT
            {
                // v0.2.12: TXT records carry the printer's mDNS UUID,
                // serial, MAC and admin URL. The admin URL is often the
                // EWS base URL directly (`adminurl=http://192.168.1.99/`).
                var txt = ParseTxtRecord(payload, i, rdlen);
                if (!string.IsNullOrEmpty(txt))
                    txtByInstance[StripTrailingDot(rname)] = txt;
            }
            i += rdlen;
        }

        foreach (var instance in ptrNames)
        {
            var inst = StripTrailingDot(instance);
            if (!seenInstances.Add(inst)) continue;
            // Try to resolve via SRV + A records
            if (srvByInstance.TryGetValue(inst, out var srv))
            {
                if (aByHost.TryGetValue(srv.host, out var ip))
                {
                    var txt = txtByInstance.TryGetValue(inst, out var t) ? t : null;
                    var fields = ParseTxtFields(txt);
                    sink.Add(new DiscoveredNetworkPrinter(
                        Name: inst,
                        IpAddress: ip.ToString(),
                        Port: srv.port,
                        IppUrl: $"ipp://{ip}:{srv.port}/ipp/print",
                        Uuid: fields.uuid,
                        Serial: fields.serial,
                        Mac: fields.mac,
                        RawTxt: txt));
                    continue;
                }
            }
            // Fallback: try to find any A/AAAA for a host that ends with the instance's
            // first label. This is a weak match but better than nothing.
            var firstLabel = inst.Split('.')[0];
            foreach (var kv in aByHost)
            {
                if (kv.Key.StartsWith(firstLabel, StringComparison.OrdinalIgnoreCase))
                {
                    sink.Add(new DiscoveredNetworkPrinter(
                        Name: inst,
                        IpAddress: kv.Value.ToString(),
                        Port: 631,
                        IppUrl: $"ipp://{kv.Value}:631/ipp/print"));
                    break;
                }
            }
        }
    }

    private static string StripTrailingDot(string s) => s.EndsWith('.') ? s[..^1] : s;

    /// <summary>
    /// Decodes a TXT RDATA blob into a single string with all key=value pairs
    /// (joined by ' '). RFC 6763 says each TXT record is one or more
    /// length-prefixed character-strings; an IPP printer typically uses a
    /// single string with all pairs separated by spaces.
    /// </summary>
    private static string ParseTxtRecord(byte[] payload, int off, int rdlen)
    {
        if (rdlen <= 0 || off + rdlen > payload.Length) return string.Empty;
        var sb = new System.Text.StringBuilder(rdlen);
        int end = off + rdlen;
        while (off < end)
        {
            int len = payload[off];
            if (off + 1 + len > end) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(System.Text.Encoding.UTF8.GetString(payload, off + 1, len));
            off += 1 + len;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pulls the fields HSA uses for matching: <c>uuid</c> (WSD UUID, matches
    /// the WSD Port Monitor's per-port <c>Printer UUID</c>), <c>serial</c>
    /// (matches the USB iSerial when we can read it), and <c>mac</c>.
    /// </summary>
    internal static (string? uuid, string? serial, string? mac) ParseTxtFields(string? txt)
    {
        if (string.IsNullOrEmpty(txt)) return (null, null, null);
        string? uuid = null, serial = null, mac = null;
        foreach (var token in txt.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            // Unwrap the urn:uuid: prefix that IPP/WSD printers use.
            if (key.Equals("uuid", StringComparison.OrdinalIgnoreCase))
            {
                uuid = value.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
                    ? value[9..] : value;
            }
            else if (key.Equals("serial", StringComparison.OrdinalIgnoreCase))
            {
                serial = value;
            }
            else if (key.Equals("mac", StringComparison.OrdinalIgnoreCase))
            {
                mac = value;
            }
        }
        return (uuid, serial, mac);
    }

    /// <summary>
    /// Discovers the WSD-Print XAddr for a WSD-USB printer. Reads the device's UUID
    /// from the WSD Port Monitor's port config and constructs the canonical XAddr
    /// (http://&lt;uuid&gt;/PrintService). The WSD Port Monitor's SpoolerApi or
    /// WSD-Print service proxy is expected to forward WSD-Print requests on this URL
    /// to the device over WSD-over-USB.
    ///
    /// Returns null for printers that aren't WSD-USB (no WSD Port Monitor entry).
    /// The returned URL may not be reachable via TCP from the host; the WSD-Print
    /// source will return an empty list if the connection fails, in which case the
    /// Supplies UI will fall back to the "WSD-USB not supported" status row.
    /// </summary>
    public string? FindWsdUsbXAddr(PrinterInfo printer)
    {
        if (printer is null) return null;
        if (!printer.PortName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            // The WSD Port Monitor stores per-port config including the Printer UUID.
            // Path: HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\<PortName>
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\{printer.PortName}");
            if (key is null) return null;
            var uuid = key.GetValue("Printer UUID") as string;
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            // The GUID might be in the form "46d67f11-480c-4b4e-b9dd-f8f60a82c3ba" or
            // wrapped in URN form. Strip URN prefix if present.
            uuid = uuid.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase).Trim();
            return $"http://{uuid}/PrintService";
        }
        catch
        {
            return null;
        }
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

    private enum DnsRecordType : ushort { A = 1, AAAA = 28, PTR = 12, TXT = 16, SRV = 33 }

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
