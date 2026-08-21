using System.Management;
using HSA.Models;

namespace HSA.Native;

/// <summary>
/// WMI queries over root\cimv2 for printer, driver, and PnP device info.
/// Reference: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/operating-system-classes
/// </summary>
public static class WmiHelper
{
    private static ManagementObjectCollection Query(string wql, string scope = @"root\cimv2")
    {
        var searcher = new ManagementObjectSearcher(scope, wql);
        return searcher.Get();
    }

    /// <summary>
    /// Returns raw Win32_Printer rows. Status field maps a numeric value to a PrinterStatus enum.
    /// </summary>
    public static List<Win32PrinterRow> QueryPrinters()
    {
        var result = new List<Win32PrinterRow>();
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT Name, ShareName, PortName, DriverName, Manufacturer, Description, Status, StatusInfo, " +
            "Default, Network, Local, Shared, Location, Comment, WorkOffline, DetectedErrorState, " +
            "PrinterStatus, ErrorState, ExtendedPrinterStatus " +
            "FROM Win32_Printer");
        foreach (ManagementObject mo in searcher.Get())
        {
            result.Add(new Win32PrinterRow
            {
                Name = (string)mo["Name"],
                ShareName = mo["ShareName"] as string ?? string.Empty,
                PortName = mo["PortName"] as string ?? string.Empty,
                DriverName = mo["DriverName"] as string ?? string.Empty,
                Manufacturer = (mo["Manufacturer"] as string ?? string.Empty).Trim(),
                Description = mo["Description"] as string ?? string.Empty,
                Status = mo["Status"] as string ?? string.Empty,
                StatusInfo = (uint)(mo["StatusInfo"] ?? 0u),
                IsDefault = (bool)(mo["Default"] ?? false),
                IsNetwork = (bool)(mo["Network"] ?? false),
                IsLocal = (bool)(mo["Local"] ?? false),
                IsShared = (bool)(mo["Shared"] ?? false),
                Location = mo["Location"] as string ?? string.Empty,
                Comment = mo["Comment"] as string ?? string.Empty,
                WorkOffline = (bool)(mo["WorkOffline"] ?? false),
                DetectedErrorState = (uint)(mo["DetectedErrorState"] ?? 0u),
                PrinterStatusRaw = (uint)(mo["PrinterStatus"] ?? 0u),
                ErrorStateRaw = (uint)(mo["ErrorState"] ?? 0u),
                ExtendedPrinterStatus = (uint)(mo["ExtendedPrinterStatus"] ?? 0u)
            });
        }
        return result;
    }

    /// <summary>
    /// Returns installed Windows print drivers (Win32_PrinterDriver). These are spooler-side drivers,
    /// not the same as the driver store. We use it to map printer â†’ driver and to delete drivers.
    /// </summary>
    public static List<Win32PrinterDriverRow> QueryPrinterDrivers()
    {
        var result = new List<Win32PrinterDriverRow>();
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT Name, FilePath, INFName, DriverPath, ConfigFile, HelpFile, MonitorName, " +
            "DefaultDataType, StartMode, SupportedPlatform, Version, ManufacturerName " +
            "FROM Win32_PrinterDriver");
        foreach (ManagementObject mo in searcher.Get())
        {
            result.Add(new Win32PrinterDriverRow
            {
                Name = (string)mo["Name"],
                FilePath = mo["FilePath"] as string ?? string.Empty,
                InfName = mo["INFName"] as string ?? string.Empty,
                DriverPath = mo["DriverPath"] as string ?? string.Empty,
                ConfigFile = mo["ConfigFile"] as string ?? string.Empty,
                HelpFile = mo["HelpFile"] as string ?? string.Empty,
                MonitorName = mo["MonitorName"] as string ?? string.Empty,
                DefaultDataType = mo["DefaultDataType"] as string ?? string.Empty,
                StartMode = mo["StartMode"] as string ?? string.Empty,
                SupportedPlatform = mo["SupportedPlatform"] as string ?? string.Empty,
                Version = mo["Version"] as string ?? string.Empty,
                ManufacturerName = mo["ManufacturerName"] as string ?? string.Empty
            });
        }
        return result;
    }

    /// <summary>
    /// Returns PnP device drivers (the class driver in driver store, not the spooler wrapper).
    /// Used to find which driver packages are installed for HP.
    /// </summary>
    public static List<Win32PnPSignedDriverRow> QueryPnPSignedDrivers()
    {
        var result = new List<Win32PnPSignedDriverRow>();
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT DeviceName, DeviceClass, DeviceID, DriverDate, DriverVersion, DriverProviderName, " +
            "InfName, IsSigned, Signer, HardwareID, CompatibleID, LocationPath, ClassGuid " +
            "FROM Win32_PnPSignedDriver");
        foreach (ManagementObject mo in searcher.Get())
        {
            result.Add(new Win32PnPSignedDriverRow
            {
                DeviceName = mo["DeviceName"] as string ?? string.Empty,
                DeviceClass = mo["DeviceClass"] as string ?? string.Empty,
                DeviceID = mo["DeviceID"] as string ?? string.Empty,
                DriverDate = mo["DriverDate"] as string ?? string.Empty,
                DriverVersion = mo["DriverVersion"] as string ?? string.Empty,
                DriverProviderName = mo["DriverProviderName"] as string ?? string.Empty,
                InfName = mo["InfName"] as string ?? string.Empty,
                IsSigned = (bool)(mo["IsSigned"] ?? false),
                Signer = mo["Signer"] as string ?? string.Empty,
                HardwareID = (mo["HardwareID"] as string[] ?? Array.Empty<string>()).ToList(),
                CompatibleID = (mo["CompatibleID"] as string[] ?? Array.Empty<string>()).ToList(),
                LocationPath = mo["LocationPath"] as string ?? string.Empty,
                ClassGuid = mo["ClassGuid"] as string ?? string.Empty
            });
        }
        return result;
    }

    /// <summary>
    /// Returns the PnP instance IDs (DeviceID) of every device that is bound to a given
    /// driver INF. These are the IDs you pass to `pnputil /remove-device &lt;id&gt;` to fully
    /// unregister a device - including its service / Enum / Print Spooler entries in
    /// the registry.
    /// </summary>
    public static IReadOnlyList<string> QueryPnpInstanceIdsForInf(string infName)
    {
        if (string.IsNullOrWhiteSpace(infName)) return Array.Empty<string>();
        var result = new List<string>();
        // WQL escape: backslash and quote. InfName is just a filename so it shouldn't
        // contain either, but we still escape defensively.
        var safe = infName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            $"SELECT DeviceID FROM Win32_PnPSignedDriver WHERE InfName = \"{safe}\"");
        foreach (ManagementObject mo in searcher.Get())
        {
            var id = mo["DeviceID"] as string;
            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Returns USB-attached HP devices (PnPEntities). Used to detect a just-connected printer.
    /// </summary>
    public static List<Win32PnPEntityRow> QueryUsbHpDevices()
    {
        var result = new List<Win32PnPEntityRow>();
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT Name, Caption, Description, DeviceID, Manufacturer, PNPClass, PNPClassGuid, " +
            "Present, Status, HardwareID, CompatibleID " +
            "FROM Win32_PnPEntity " +
            "WHERE PNPClass = 'Printer' OR PNPClass = 'USB'");
        foreach (ManagementObject mo in searcher.Get())
        {
            var name = mo["Name"] as string ?? string.Empty;
            var manufacturer = mo["Manufacturer"] as string ?? string.Empty;
            var hwid = (mo["HardwareID"] as string[] ?? Array.Empty<string>()).ToList();
            if (!IsHp(name, manufacturer, hwid)) continue;

            result.Add(new Win32PnPEntityRow
            {
                Name = name,
                Caption = mo["Caption"] as string ?? string.Empty,
                Description = mo["Description"] as string ?? string.Empty,
                DeviceID = mo["DeviceID"] as string ?? string.Empty,
                Manufacturer = manufacturer,
                PnpClass = mo["PNPClass"] as string ?? string.Empty,
                ClassGuid = mo["PNPClassGuid"] as string ?? string.Empty,
                Present = (bool)(mo["Present"] ?? false),
                Status = mo["Status"] as string ?? string.Empty,
                HardwareID = hwid,
                CompatibleID = (mo["CompatibleID"] as string[] ?? Array.Empty<string>()).ToList()
            });
        }
        return result;
    }

    private static bool IsHp(string name, string manufacturer, IReadOnlyList<string> hwid)
    {
        if (manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("HP", StringComparison.OrdinalIgnoreCase)) return true;
        if (hwid.Any(h => h.Contains("hp", StringComparison.OrdinalIgnoreCase) || h.Contains("Hewlett", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }
}

public sealed class Win32PrinterRow
{
    public string Name { get; init; } = string.Empty;
    public string ShareName { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public string DriverName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public uint StatusInfo { get; init; }
    public bool IsDefault { get; init; }
    public bool IsNetwork { get; init; }
    public bool IsLocal { get; init; }
    public bool IsShared { get; init; }
    public string Location { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public bool WorkOffline { get; init; }
    public uint DetectedErrorState { get; init; }
    public uint PrinterStatusRaw { get; init; }
    public uint ErrorStateRaw { get; init; }
    public uint ExtendedPrinterStatus { get; init; }
}

public sealed class Win32PrinterDriverRow
{
    public string Name { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string InfName { get; init; } = string.Empty;
    public string DriverPath { get; init; } = string.Empty;
    public string ConfigFile { get; init; } = string.Empty;
    public string HelpFile { get; init; } = string.Empty;
    public string MonitorName { get; init; } = string.Empty;
    public string DefaultDataType { get; init; } = string.Empty;
    public string StartMode { get; init; } = string.Empty;
    public string SupportedPlatform { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ManufacturerName { get; init; } = string.Empty;
}

public sealed class Win32PnPSignedDriverRow
{
    public string DeviceName { get; init; } = string.Empty;
    public string DeviceClass { get; init; } = string.Empty;
    public string DeviceID { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public string DriverProviderName { get; init; } = string.Empty;
    public string InfName { get; init; } = string.Empty;
    public bool IsSigned { get; init; }
    public string Signer { get; init; } = string.Empty;
    public List<string> HardwareID { get; init; } = new();
    public List<string> CompatibleID { get; init; } = new();
    public string LocationPath { get; init; } = string.Empty;
    public string ClassGuid { get; init; } = string.Empty;
}

public sealed class Win32PnPEntityRow
{
    public string Name { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DeviceID { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string PnpClass { get; init; } = string.Empty;
    public string ClassGuid { get; init; } = string.Empty;
    public bool Present { get; init; }
    public string Status { get; init; } = string.Empty;
    public List<string> HardwareID { get; init; } = new();
    public List<string> CompatibleID { get; init; } = new();
}
