namespace PrintHub.Core.Usb;

public enum UsbHttpKind
{
    None,
    /// <summary>Standard IPP-USB (class 07, subclass 01, protocol 04): HTTP tunnelled over bulk pipes.</summary>
    IppUsb,
    /// <summary>HP vendor web-services interface (class FF, subclass 04, protocol 01).</summary>
    HpWebServices,
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
    /// <summary>True when the interface is present and bound to WinUSB, so it can be opened without changing drivers.</summary>
    public bool Openable => Present && DevicePath is not null && Service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase);

    public string Describe() =>
        $"{VendorId:X4}:{ProductId:X4} MI_{InterfaceNumber:00} {Name} [{Class:X2}/{SubClass:X2}/{Protocol:X2}] svc={Service} {HttpKind} present={Present} openable={Openable}";
}
