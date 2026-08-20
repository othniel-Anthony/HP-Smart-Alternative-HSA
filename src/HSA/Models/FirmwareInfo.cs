namespace HSA.Models;

public sealed class FirmwareInfo
{
    public string PrinterName { get; set; } = string.Empty;
    public string? CurrentVersion { get; set; }
    public string? ModelIdentifier { get; set; }   // e.g. "HP LaserJet Pro M404dn"
    public string? Hostname { get; set; }
    public string? IpAddress { get; set; }
    public FirmwareDetectionMethod DetectionMethod { get; set; }
    public FirmwareUpdateCapability UpdateCapability { get; set; }
    public string? HpSupportUrl { get; set; }      // deep link to HP support
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public string CurrentVersionDisplay => CurrentVersion ?? "Unknown";
    public string DetectionMethodDisplay => DetectionMethod switch
    {
        FirmwareDetectionMethod.Ipp => "IPP",
        FirmwareDetectionMethod.Snmp => "SNMP",
        FirmwareDetectionMethod.EmbeddedWebServer => "Embedded Web Server",
        _ => "Unknown"
    };
    public string UpdateCapabilityDisplay => UpdateCapability switch
    {
        FirmwareUpdateCapability.IppSystemServices => "Push via IPP System Services (PWG 5100.11)",
        FirmwareUpdateCapability.HpSupportLink => "Open HP support page",
        FirmwareUpdateCapability.ManualOnly => "Manual (HP Smart only)",
        _ => "Unknown"
    };
}

public enum FirmwareDetectionMethod
{
    Unknown,
    Ipp,
    Snmp,
    EmbeddedWebServer
}

public enum FirmwareUpdateCapability
{
    Unknown,
    /// <summary>Printer implements PWG 5100.11 System Services and we can push firmware over IPP.</summary>
    IppSystemServices,
    /// <summary>We can read version but cannot push; user clicks through to HP's support page.</summary>
    HpSupportLink,
    /// <summary>Printer model requires HP Smart or Web Jetadmin.</summary>
    ManualOnly
}
