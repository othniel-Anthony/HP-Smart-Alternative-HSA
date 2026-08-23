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
    private readonly PrinterEndpointDiscovery _endpoint;
    private readonly ILogger<EwsDiscoveryService> _log;

    public EwsDiscoveryService(
        EwsService ews,
        SettingsService settings,
        PrinterEndpointDiscovery endpoint,
        ILogger<EwsDiscoveryService> log)
    {
        _ews = ews;
        _settings = settings;
        _endpoint = endpoint;
        _log = log;
    }

    /// <summary>
    /// Returns the EWS base URL for the printer, or null if no candidate worked.
    /// The user-pinned URL always wins — unless <paramref name="ignorePin"/> is
    /// true (used by the launch-time self-healing path to re-discover after a
    /// pin fails verification).
    /// </summary>
    public async Task<string?> DiscoverAsync(
        PrinterInfo printer,
        CancellationToken ct = default,
        bool ignorePin = false)
    {
        if (printer is null) return null;

        // 1) User-pinned URL (always wins — unless we're re-discovering).
        if (!ignorePin && TryGetPinned(printer, out var pinned))
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

        // 3) mDNS browse with WSD-UUID / name matching (v0.2.10 + v0.2.12).
        //    See DiscoverByMdnsNameAsync for details.
        try
        {
            var byMdns = await DiscoverByMdnsNameAsync(printer, ct);
            if (byMdns is not null)
            {
                _log.LogInformation("EWS discovery for {Printer}: matched by mDNS {Url}", printer.Name, byMdns);
                return byMdns;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS EWS discovery failed for {Printer}", printer.Name);
        }

        // 4) Subnet scan. Many HP printers (especially AiOs that are also on Wi-Fi)
        // don't advertise via mDNS but DO respond on TCP 80. v0.2.10 makes this
        // name-aware: candidates are scored by how many of the printer's name
        // tokens appear in the response body, so if you have multiple HP
        // printers on the network the scan picks the one whose EWS page actually
        // mentions "OfficeJet" and "9730", not just any HP EWS.
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

    private static string? TryGetWsdPortUuid(PrinterInfo printer) => GetWsdPortUuid(printer);

    /// <summary>
    /// v0.2.12: read the WSD Port Monitor's per-port <c>Printer UUID</c> for
    /// this printer. This is the unique UUID the printer advertises via
    /// mDNS/WSD-Print; matching it against an mDNS browse result that
    /// includes the printer's TXT record gives us a high-confidence link
    /// between the USB-attached spooler queue and the printer's network IP.
    /// Public so <c>App.AutoDiscoverAndPinEwsAsync</c> and other callers
    /// can use it as a fingerprint.
    /// </summary>
    public static string? GetWsdPortUuid(PrinterInfo printer)
    {
        try
        {
            if (printer is null) return null;
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
    /// v0.2.12: browses `_ipp._tcp.local` and matches results in this order:
    ///   1. **WSD UUID match** — the printer's WSD Port Monitor UUID (read
    ///      from the registry) is the unique id the same device advertises
    ///      via mDNS. A UUID match is the highest-confidence way to link the
    ///      USB-attached spooler queue to a network addressable printer.
    ///   2. **Name-token match** — fall back to scoring by name overlap, the
    ///      v0.2.10 behavior.
    /// Returns the URL of the best-matching instance, or null.
    /// </summary>
    private async Task<string?> DiscoverByMdnsNameAsync(PrinterInfo printer, CancellationToken ct)
    {
        try
        {
            var discovered = await _endpoint.BrowseAsync(ct);
            if (discovered is null || discovered.Count == 0) return null;

            // Pass 1: WSD UUID match. v0.2.12 captures the TXT record so we
            // can compare the printer's WSD UUID to the UUID the same
            // device broadcasts over mDNS.
            var wsdUuid = GetWsdPortUuid(printer);
            if (!string.IsNullOrEmpty(wsdUuid))
            {
                foreach (var d in discovered)
                {
                    if (string.IsNullOrEmpty(d.Uuid)) continue;
                    if (string.Equals(d.Uuid, wsdUuid, StringComparison.OrdinalIgnoreCase))
                    {
                        var url = $"http://{d.IpAddress}/";
                        _log.LogInformation("mDNS UUID match: {Printer} -> {Name} @ {Url} (uuid={Uuid})",
                            printer.Name, d.Name, url, wsdUuid);
                        return url;
                    }
                }
            }

            // Pass 2: name-token match.
            var tokens = ExtractNameTokens(printer);
            if (tokens.Count > 0)
            {
                (string Name, string Url, int Score)? best = null;
                foreach (var d in discovered)
                {
                    if (string.IsNullOrEmpty(d.IpAddress)) continue;
                    var score = ScoreMatch(d.Name + " " + d.IppUrl, tokens);
                    if (score > 0 && (best is null || score > best.Value.Score))
                    {
                        var url = $"http://{d.IpAddress}/";
                        best = (d.Name, url, score);
                    }
                }
                if (best is not null)
                {
                    _log.LogInformation("mDNS-by-name: matched {Name} -> {Url} (score {Score}, tokens {Tokens})",
                        best.Value.Name, best.Value.Url, best.Value.Score, string.Join(",", tokens));
                    return best.Value.Url;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS-by-name browse failed for {Printer}", printer.Name);
        }
        return null;
    }

    /// <summary>
    /// Last-resort discovery: probe every host in the local /24 on TCP 80 and
    /// score each response by how many of the printer's name tokens appear in
    /// the EWS body. v0.2.10 is name-aware: if your 9730 lives on the network,
    /// its EWS home page will contain "OfficeJet" and "9730" and that's how
    /// we pick it out of every other HP EWS that might be on the same subnet.
    ///
    /// ~250 probes at 300ms each = ~30s worst case. The connect times out fast
    /// for non-printer IPs (closed port or no response) so most are cheap.
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
        var subnet = $"{prefix[0]}.{prefix[1]}.{prefix[2]}";
        var tokens = ExtractNameTokens(printer);
        _log.LogInformation("Subnet scan: probing {Subnet}.0/24 for {Printer} (tokens: {Tokens})",
            subnet, printer.Name, string.Join(",", tokens));

        // Candidates with a score; the highest-scoring URL wins.
        var candidates = new System.Collections.Concurrent.ConcurrentBag<(string Url, int Score, string Reason)>();
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
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!LooksLikeHpEws(body)) return;
                // EWS confirmed; now score against the printer's name tokens.
                var score = ScoreMatch(body, tokens);
                if (tokens.Count == 0)
                {
                    // No usable tokens (e.g. the spooler name was just a hardware ID
                    // with no model info). Fall back to accepting any HP EWS so the
                    // user at least has something to click.
                    score = 1;
                }
                candidates.Add((url, score, $"name tokens matched"));
                _log.LogInformation("Subnet scan: HP EWS at {Url}, score {Score}", url, score);
            }
            catch { /* closed port / timeout / DNS — keep scanning */ }
        }, ct)).ToList();

        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* timeout — fine, we may have a candidate already */ }

        if (candidates.IsEmpty) return null;
        return candidates.OrderByDescending(c => c.Score).First().Url;
    }

    private static bool LooksLikeHpEws(string body) => LooksLikeHpEwsPublic(body);

    /// <summary>
    /// Pure "is this body a real HP EWS?" check, with no name dependency.
    /// v0.2.9+: require a positive /DevMgmt/ EWS signature. Bare "HP" is not
    /// enough — that was the v0.2.6 false-positive source (router admin pages).
    /// Public so <c>App.AutoDiscoverAndPinEwsAsync</c> can use it to verify
    /// existing pins. v0.2.11.
    /// </summary>
    public static bool LooksLikeHpEwsPublic(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        return body.Contains("/DevMgmt/", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Embedded Web Server", StringComparison.OrdinalIgnoreCase)
            || body.Contains("hp/device/", StringComparison.OrdinalIgnoreCase)
            || body.Contains("hp_ews", StringComparison.OrdinalIgnoreCase)
            || body.Contains("HP EWS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the meaningful identifying tokens out of the printer's spooler
    /// name and model. Strips common filler ("HP", "series", "All-in-One"),
    /// the brand prefix, and very short or all-numeric tokens. The remaining
    /// tokens are used both for mDNS name matching and for scoring the
    /// subnet scan — "OfficeJet", "Pro", "9730" survive, "HP" and "series"
    /// don't.
    /// </summary>
    public static List<string> ExtractNameTokens(PrinterInfo printer)
    {
        var name = (printer.Name ?? string.Empty) + " " + (printer.Model ?? string.Empty);
        // Filler that would match too many printers.
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HP", "series", "Series", "the", "and", "for", "printer", "printers",
            "all-in-one", "All-in-One", "AIO", "laserjet", "LaserJet", "officejet", "OfficeJet",
            // "OfficeJet" / "LaserJet" are HP family names — they appear in EVERY
            // OfficeJet / LaserJet's EWS, so they're not useful as a unique
            // fingerprint. But we keep the model number / suffix.
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var out2 = new List<string>();
        foreach (var raw in name.Split(new[] { ' ', '\t', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim(',', '.', ';', ':');
            if (token.Length < 3) continue;
            if (stop.Contains(token)) continue;
            // Skip tokens that are JUST a brand prefix like "HPI02082C" — those
            // are the spooler's hardware IDs, not the human-readable name. We
            // only filter if the token starts with a known brand prefix and has
            // no alphabetic characters beyond the brand.
            if (token.StartsWith("HPI", StringComparison.OrdinalIgnoreCase)
                && token.Length > 3
                && !token.Any(char.IsLetter) == false  // has at least one letter
                && !token.Substring(3).Any(char.IsLetter))
            {
                // looks like HPI02082C — skip
                continue;
            }
            if (seen.Add(token)) out2.Add(token);
        }
        return out2;
    }

    /// <summary>Counts how many of the printer's name tokens appear in <paramref name="haystack"/>.</summary>
    public static int ScoreMatch(string haystack, IReadOnlyCollection<string> tokens)
    {
        if (string.IsNullOrEmpty(haystack) || tokens.Count == 0) return 0;
        int score = 0;
        foreach (var t in tokens)
        {
            if (haystack.Contains(t, StringComparison.OrdinalIgnoreCase))
                score++;
        }
        return score;
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
