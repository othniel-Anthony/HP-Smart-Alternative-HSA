namespace HSA.Models;

/// <summary>
/// One printer discovered via mDNS (Bonjour) browse. Returned by
/// <see cref="Services.PrinterEndpointDiscovery.BrowseAsync"/>.
/// The user can choose to add these as installable printers in the
/// Printers tab (a future enhancement) or use the IPP URL for direct
/// supply / firmware queries.
/// </summary>
public sealed record DiscoveredNetworkPrinter(
    string Name,
    string IpAddress,
    int Port,
    string IppUrl);
