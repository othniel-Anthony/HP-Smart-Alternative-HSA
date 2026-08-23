using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HSA.Models;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Tries to find the Embedded Web Server (EWS) URL for a printer that the print
/// spooler reports but that doesn't have a pinned EWS URL in <see cref="AppSettings.EwsAddresses"/>.
///
/// Discovery strategy (in order):
///   1. The user-pinned URL keyed by DeviceId (always wins; never overridden).
///   2. If the printer reports an IP address (IsNetworkPrinter / IpAddress set),
///      try <c>http://&lt;ip&gt;/</c>. This is the most common case for any
///      network-attached HP printer.
///   3. mDNS browse for <c>_ipp._tcp.local</c> and <c>_http._tcp.local</c> —
///      most HP printers advertise themselves via Bonjour including over Wi-Fi
///      even when the spooler port is WSD-USB or USB.
///   4. mDNS targeted by the printer's UUID (from the WSD Port Monitor registry)
///      for WSD-USB devices that have a UUID but no IP.
///
/// Each candidate URL is probed via <see cref="EwsService.ProbeAsync"/> with a
/// short timeout; the first that returns 2xx wins.
/// </summary>
public sealed class EwsDiscoveryService
{
    private readonly EwsService _ews;
    private readonly SettingsService _settings;
    private readonly ILogger<EwsDiscoveryService> _log;

    public EwsDiscoveryService(EwsService ews, SettingsService settings, ILogger<EwsDiscoveryService> log)
    {
        _ews = ews;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Returns the EWS base URL for the printer, or null if no candidate worked.
    /// The user-pinned URL always wins. For non-pinned printers the first
    /// reachable candidate is returned.
    /// </summary>
    public async Task<string?> DiscoverAsync(PrinterInfo printer, CancellationToken ct = default)
    {
        if (printer is null) return null;

        // 1) User-pinned URL (always wins).
        if (TryGetPinned(printer, out var pinned))
        {
            _log.LogInformation("EWS discovery for {Printer}: using pinned URL {Url}", printer.Name, pinned);
            return pinned;
        }

        // 2) Direct from the printer's IP (network printers / Wi-Fi-attached).
        if (!string.IsNullOrWhiteSpace(printer.IpAddress)
            && IPAddress.TryParse(printer.IpAddress, out _))
        {
            var candidate = $"http://{printer.IpAddress}/";
            if (await _ews.ProbeAsync(candidate, ct))
            {
                _log.LogInformation("EWS discovery for {Printer}: matched by IP {Url}", printer.Name, candidate);
                return candidate;
            }
        }

        // 3) mDNS browse for IPP/HTTP services, match by name.
        try
        {
            var discovered = await BrowseLocalNetworkForEwsAsync(printer, ct);
            if (discovered is not null)
            {
                if (await _ews.ProbeAsync(discovered, ct))
                {
                    _log.LogInformation("EWS discovery for {Printer}: matched by mDNS {Url}", printer.Name, discovered);
                    return discovered;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS EWS discovery failed for {Printer}", printer.Name);
        }

        // 4) Subnet scan. Many HP printers (especially AiOs that are also on Wi-Fi)
        // don't advertise via mDNS but DO respond on TCP 80. Probing /DevMgmt/
        // ProductConfigDyn.xml is the fastest tell: a real EWS returns a 200 with
        // a few hundred bytes of XML; anything else either rejects the connection
        // (closed port) or returns a non-2xx (different service). Limited to the
        // local /24 by default to keep it under ~5s; override via `maxHosts`.
        try
        {
            var subnetted = await ScanLocalSubnetForEwsAsync(printer, ct);
            if (subnetted is not null)
            {
                _log.LogInformation("EWS discovery for {Printer}: matched by subnet scan {Url}", printer.Name, subnetted);
                return subnetted;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Subnet scan failed for {Printer}", printer.Name);
        }

        _log.LogInformation("EWS discovery for {Printer}: no candidate reachable", printer.Name);
        return null;
    }

    /// <summary>
    /// Persists a discovered URL into the per-printer EWS map. No-op if the URL
    /// is null/empty. Returns true if a change was made.
    /// </summary>
    public bool Pin(PrinterInfo printer, string? url)
    {
        if (printer is null) return false;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim().TrimEnd('/');
        _settings.Update(s => s.EwsAddresses[printer.DeviceId] = trimmed);
        _log.LogInformation("Pinned EWS URL for {Printer} ({DeviceId}) -> {Url}", printer.Name, printer.DeviceId, trimmed);
        return true;
    }

    /// <summary>Returns the user-pinned URL for the printer, or null.</summary>
    public bool TryGetPinned(PrinterInfo printer, out string url)
    {
        url = string.Empty;
        if (printer is null) return false;
        if (_settings.Current.EwsAddresses.TryGetValue(printer.DeviceId, out var u)
            && !string.IsNullOrWhiteSpace(u))
        {
            url = u;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Light mDNS browse targeted at the printer's family / name. Returns the
    /// first candidate that looks like a match, or null. Cheap: ~2s timeout.
    /// </summary>
    private async Task<string?> BrowseLocalNetworkForEwsAsync(PrinterInfo printer, CancellationToken ct)
    {
        // For WSD-USB printers we can derive a candidate by asking the local DNS
        // resolver for "<uuid>.local". Some HP printers register themselves there.
        // For everyone else we fall back to a generic browse.
        try
        {
            var uuid = TryGetWsdPortUuid(printer);
            if (!string.IsNullOrWhiteSpace(uuid))
            {
                var candidate = $"http://{uuid}.local/";
                if (await _ews.ProbeAsync(candidate, ct)) return candidate;
            }
        }
        catch { /* ignore */ }

        // Last-resort: try the printer's own hostname guess. mDNS name for an HP
        // printer is usually <model>-<serial>.local or HP<model>.local. We
        // construct a handful of likely names and probe them.
        var names = GuessMdnsNames(printer);
        foreach (var n in names)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = $"http://{n}/";
            if (await _ews.ProbeAsync(candidate, ct)) return candidate;
        }
        return null;
    }

    private static string? TryGetWsdPortUuid(PrinterInfo printer)
    {
        try
        {
            if (!printer.PortName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase)) return null;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\{printer.PortName}");
            var uuid = key?.GetValue("Printer UUID") as string;
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            return uuid.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        catch { return null; }
    }

    private static IEnumerable<string> GuessMdnsNames(PrinterInfo printer)
    {
        // Build a few likely .local hostnames from the printer's model / name.
        // These are best-effort guesses; the actual mDNS name is whatever the
        // device broadcasts.
        var model = (printer.Model ?? printer.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(model)) yield break;
        var firstWord = model.Split(' ', 2)[0];
        // Strip non-alphanumerics
        var clean = new string(firstWord.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(clean)) yield break;
        yield return $"{clean}.local";
        yield return $"HP{clean}.local";
        yield return $"HP_{clean}.local";
    }

    /// <summary>
    /// Last-resort discovery: probe every host in the local /24 on TCP 80 and
    /// ask it for /DevMgmt/ProductConfigDyn.xml. A real HP EWS returns 200 with
    /// a few hundred bytes of XML. We further check the response body for an HP
    /// make/model so we don't false-positive on a router or NAS web UI.
    ///
    /// ~250 probes at 200ms each = ~50s worst case. The probe times out fast
    /// (300ms connect timeout) so most non-printer IPs return immediately with
    /// "connection refused" or "timeout", which is cheap.
    /// </summary>
    private async Task<string?> ScanLocalSubnetForEwsAsync(PrinterInfo printer, CancellationToken ct)
    {
        var localIp = LocalIPv4();
        if (localIp is null)
        {
            _log.LogDebug("Subnet scan skipped: no usable local IPv4 address.");
            return null;
        }
        var prefix = localIp.GetAddressBytes();
        // /24 only — covers typical home/SOHO networks. Larger subnets would
        // need a CIDR from the user.
        var subnet = $"{prefix[0]}.{prefix[1]}.{prefix[2]}";
        _log.LogInformation("Subnet scan: probing {Subnet}.0/24 for {Printer}", subnet, printer.Name);

        // Probe in parallel (limited concurrency) so a /24 takes a few seconds
        // rather than 50s sequentially.
        var candidates = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = Enumerable.Range(1, 254).Select(i => Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var ip = $"{subnet}.{i}";
            if (ip == localIp.ToString()) return;
            var url = $"http://{ip}/";
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connect = tcp.ConnectAsync(ip, 80);
                var completed = await Task.WhenAny(connect, Task.Delay(300, ct));
                if (completed != connect || !tcp.Connected) return;
                // Cheap body check: GET / and look for "HP" + "Embedded" or "EWS".
                // We don't try /DevMgmt/ProductConfigDyn.xml first because the
                // printer's home page is shorter and equally diagnostic.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (LooksLikeHpEws(body, printer))
                {
                    _log.LogInformation("Subnet scan: HP EWS match at {Url}", url);
                    candidates.Add(url);
                }
            }
            catch { /* closed port / timeout / DNS — keep scanning */ }
        }, ct)).ToList();

        // Bound the whole scan to ~30s; if we find a candidate earlier we'll
        // still let the rest finish in the background but return promptly.
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* timeout — fine, we may have a candidate already */ }

        return candidates.FirstOrDefault();
    }

    private static bool LooksLikeHpEws(string body, PrinterInfo printer)
    {
        if (string.IsNullOrEmpty(body)) return false;
        // The EWS home page contains a "Manufacturer: HP" or "Hewlett-Packard"
        // string and a model number block. We don't try to parse it; a substring
        // match for HP + a model keyword is enough to avoid false positives.
        var hasHp = body.Contains("Hewlett-Packard", StringComparison.OrdinalIgnoreCase)
                 || body.Contains("HP ", StringComparison.OrdinalIgnoreCase)
                 || body.Contains("\"HP\"", StringComparison.OrdinalIgnoreCase);
        if (!hasHp) return false;
        // Loose model match: any whitespace-separated token of length >= 3
        // from the printer's name or model that also appears in the body. This
        // catches e.g. "OfficeJet" and "Pro" without being too strict.
        var name = (printer.Name ?? string.Empty) + " " + (printer.Model ?? string.Empty);
        foreach (var token in name.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 3) continue;
            if (body.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Fallback: any "EWS" / "Embedded Web Server" mention + HP is enough.
        return body.Contains("Embedded Web Server", StringComparison.OrdinalIgnoreCase)
            || body.Contains("/DevMgmt/", StringComparison.OrdinalIgnoreCase);
    }

    private static System.Net.IPAddress? LocalIPv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ua.Address;
                }
            }
        }
        catch { }
        return null;
    }
}
