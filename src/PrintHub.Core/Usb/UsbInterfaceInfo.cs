namespace PrintHub.Core.Usb;

/// <summary>How HSA gets at an interface without changing the driver Windows chose for it.</summary>
public enum UsbTransport
{
    /// <summary>Not reachable: the interface is not present, or Windows gave it to a driver HSA cannot talk through.</summary>
    None,
    /// <summary>The interface is bound to WinUSB: bulk pipes are used directly.</summary>
    WinUsb,
    /// <summary>The interface is bound to Windows' scanner driver (usbscan), which exposes it as a device file (HP's scan software does this).</summary>
    Usbscan,
}

public enum UsbHttpKind
{
    None,
    /// <summary>Standard IPP-USB (class 07, subclass 01, protocol 04): HTTP tunnelled over bulk pipes.</summary>
    IppUsb,
    /// <summary>HP vendor web-services interface (class FF, subclass 04, protocol 01).</summary>
    HpWebServices,
    /// <summary>
    /// Brother's embedded web server over USB: a vendor interface (class FF, subclass FF, protocol FF) bound to WinUSB, speaking plain HTTP. Brother's own launcher
    /// reaches it through its HttpToUsbBridge. The same class triple is also what Brother's scanner interface reports, so only a WinUSB-bound one counts (the scanner
    /// interface is bound to Windows' scanner driver).
    /// </summary>
    BrotherWebServices,
}

public sealed record UsbInterfaceInfo(
    string InstanceId,
    int VendorId,
    int ProductId,
    int? InterfaceNumber,
    string Name,
    string Service,
    byte Class, byte SubClass, byte Protocol,
    UsbHttpKind HttpKind,
    bool Present,
    string? DevicePath,
    string? ContainerId)
{
    public UsbTransport Transport =>
        Service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase) ? UsbTransport.WinUsb :
        Service.Equals("usbscan", StringComparison.OrdinalIgnoreCase) ? UsbTransport.Usbscan : UsbTransport.None;

    /// <summary>True when the interface is present and bound to WinUSB or to the scanner driver, so it can be opened without changing drivers.</summary>
    public bool Openable => Present && DevicePath is not null && Transport != UsbTransport.None;

    public string Describe() =>
        $"{VendorId:X4}:{ProductId:X4} MI_{InterfaceNumber:00} {Name} [{Class:X2}/{SubClass:X2}/{Protocol:X2}] svc={Service} via={Transport} {HttpKind} present={Present} openable={Openable}";
}
