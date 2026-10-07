using PrintHub.Core.Printing;

namespace PrintHub.Core.Discovery;

/// <summary>Works out which printers are plugged in by USB right now, and which of them HSA should pick without being asked.</summary>
public static class PrinterPicker
{
    /// <summary>
    /// Marks every device that is physically connected by USB at this moment: it has a present USB web-services interface, or a present USB
    /// print interface that matches its name or the USB port of its Windows queue (that is how printers without a USB web page are found).
    /// A queue for a printer that is unplugged is not marked.
    /// </summary>
    public static void MarkUsb(IEnumerable<PrinterDevice> devices)
    {
        List<(string Path, string Name, int Vendor, int Product, string Container)> present;
        try { present = EpsonMaintenance.PresentPrintInterfaces().Where(p => !p.Name.Contains("fax", StringComparison.OrdinalIgnoreCase)).ToList(); }
        catch { present = new(); }
        foreach (var d in devices) d.OnUsb = IsOnUsbBus(d, present);
    }

    internal static bool IsOnUsbBus(PrinterDevice d, List<(string Path, string Name, int Vendor, int Product, string Container)> present)
    {
        if (d.UsbCandidates.Any(u => u.Present)) return true;
        // A Windows queue on a USB port belongs to one physical printer (the container id of the port's device). That is exact, where names are not:
        // with three L3250s around, "EPSON L3250 Series" is the name of all of them.
        if (d.SpoolerPort is { Length: > 0 } qport && qport.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && EpsonMaintenance.ContainerOfPort(qport) is { Length: > 0 } qc)
            return present.Any(p => string.Equals(p.Container, qc, StringComparison.OrdinalIgnoreCase));
        if (present.Any(p => PrinterDevice.Similar(p.Name, d.Name) || (d.SpoolerDriver is not null && PrinterDevice.Similar(p.Name, d.SpoolerDriver)))) return true;
        if (d.SpoolerPort is { Length: > 0 } port && port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && EpsonMaintenance.ContainerOfPort(port) is { Length: > 0 } c)
            return present.Any(p => string.Equals(p.Container, c, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    /// <summary>Plugged in by USB and not switched to "use printer offline".</summary>
    public static bool IsOnlineOnUsb(PrinterDevice d) => d.OnUsb && !d.SpoolerOffline && (d.CanPrint || d.CanScan);

    static bool Same(PrinterDevice a, PrinterDevice b) => a.Id == b.Id || a.Name == b.Name || PrinterDevice.Similar(a.Name, b.Name);

    /// <summary>The Windows queue's USB port belongs to a printer that is plugged in right now: the container id of the port's device, or null.</summary>
    internal static string? LiveContainer(string? port, IReadOnlyCollection<string> presentContainers)
    {
        if (port is not { Length: > 0 } || !port.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return null;
        var c = EpsonMaintenance.ContainerOfPort(port);
        return c is { Length: > 0 } && presentContainers.Any(p => string.Equals(p, c, StringComparison.OrdinalIgnoreCase)) ? c : null;
    }

    /// <summary>Container ids of the USB printers plugged in right now.</summary>
    internal static List<string> PresentContainers()
    {
        try { return EpsonMaintenance.PresentPrintInterfaces().Where(p => p.Container.Length > 0).Select(p => p.Container).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new(); }
    }

    /// <summary>
    /// The USB printer to switch to, or null to leave the selection alone.
    /// Until the user has picked a printer by hand, the online USB printer wins over whatever was remembered (the remembered one, then the
    /// Windows default, break a tie between several). After a manual pick, only a printer that has just been plugged in (it was not on USB in
    /// the previous search, <paramref name="previousUsbNames"/>) is switched to, so a search never takes the choice away from the user.
    /// </summary>
    public static PrinterDevice? PickUsb(IReadOnlyList<PrinterDevice> devices, PrinterDevice? current, string? rememberedId, string? defaultQueue,
        bool userChose, IReadOnlyCollection<string> previousUsbNames)
    {
        var usb = devices.Where(IsOnlineOnUsb).ToList();
        if (usb.Count == 0) return null;

        if (!userChose)
            return usb.FirstOrDefault(d => d.Id == rememberedId)
                   ?? (current is null ? null : usb.FirstOrDefault(d => d.Id == current.Id || d.Name == current.Name) ?? usb.FirstOrDefault(d => Same(d, current)))
                   ?? usb.FirstOrDefault(d => d.SpoolerName is not null && d.SpoolerName == defaultQueue)
                   ?? usb[0];

        // Not seen before: its exact name was not there. A name that is only similar to a seen one is the same printer spelled differently, unless another
        // printer of the same model is plugged in too (two L3250s are both "EPSON L3250 Series ...").
        return usb.FirstOrDefault(d => !previousUsbNames.Any(n => n == d.Name)
            && !(previousUsbNames.Any(n => PrinterDevice.Similar(n, d.Name)) && usb.Count(o => PrinterDevice.Similar(o.Name, d.Name)) == 1));
    }
}
