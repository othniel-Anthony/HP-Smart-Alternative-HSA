using System.Net;
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
}
