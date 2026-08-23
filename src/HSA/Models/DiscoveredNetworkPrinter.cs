namespace HSA.Models;

/// <summary>
/// One printer discovered via mDNS (Bonjour) browse. Returned by
/// <see cref="Services.PrinterEndpointDiscovery.BrowseAsync"/>.
/// The user can choose to add these as installable printers in the
/// Printers tab (a future enhancement) or use the IPP URL for direct
/// supply / firmware queries.
///
/// v0.2.12: also carries the parsed mDNS TXT record. Most HP printers
/// publish a TXT record with `uuid=`, `serial=`, `mac=` and `ty=` fields;
/// the UUID is the WSD Port Monitor UUID the same printer advertises over
/// WSD-USB, so a host that sees the printer on both transports can
/// correlate the two. <see cref="Uuid"/> / <see cref="Serial"/> are
/// pre-extracted for fast matching.
/// </summary>
public sealed record DiscoveredNetworkPrinter(
    string Name,
    string IpAddress,
    int Port,
    string IppUrl,
    string? Uuid = null,
    string? Serial = null,
    string? Mac = null,
    string? RawTxt = null);
