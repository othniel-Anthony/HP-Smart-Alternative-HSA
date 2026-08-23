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
    /// Eagerly enumerates instances of a WMI class into a list. Using direct CIM
    /// access (the same path PowerShell's Get-CimInstance uses) to avoid the
    /// WQL "Invalid query" (0x80041017) parser failure we hit on some hosts.
    /// Eager materialization (vs yield return) is intentional: it ensures the
    /// underlying WMI enumerator is fully consumed and disposed before we
    /// return, so each ManagementObject is freshly owned by the caller and not
    /// tied to a disposed collection.
    /// </summary>
    private static List<ManagementObject> EnumerateInstances(string wmiClassName, string scope = @"root\cimv2")
    {
        var result = new List<ManagementObject>();
        using var mc = new ManagementClass(scope, wmiClassName, null);
        foreach (ManagementObject mo in mc.GetInstances())
        {
            // Clone each instance so it's a fresh, independent ManagementObject that
            // we own. Otherwise the consumer reads from a disposed object after the
            // using-scope around mc tears down the underlying collection.
            var copy = (ManagementObject)mo.Clone();
            result.Add(copy);
        }
        return result;
    }

    /// <summary>
    /// Safe property read: returns <paramref name="defaultValue"/> when the property
    /// is missing on the WMI class instance (some Windows builds / driver packages
    /// don't expose every documented property, and the raw indexer throws
    /// "Not found" 0x80041002 in that case). Used to make WMI reads robust across
    /// OEM and version skew.
    /// </summary>
    private static T WmiGet<T>(ManagementObject mo, string name, T defaultValue = default!)
    {
        try
        {
            var prop = mo.Properties[name];
            if (prop is null || prop.Value is null) return defaultValue;
            if (prop.Value is T direct) return direct;
            return (T)Convert.ChangeType(prop.Value, typeof(T))!;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string WmiStr(ManagementObject mo, string name)
        => WmiGet<object>(mo, name)?.ToString() ?? string.Empty;

    private static uint WmiUint(ManagementObject mo, string name)
        => Convert.ToUInt32(WmiGet<object>(mo, name, 0u) ?? 0u);

    private static bool WmiBool(ManagementObject mo, string name)
        => Convert.ToBoolean(WmiGet<object>(mo, name, false) ?? false);

    private static List<string> WmiArr(ManagementObject mo, string name)
        => WmiGet<string[]>(mo, name, Array.Empty<string>())?.ToList() ?? new List<string>();


    /// <summary>
    /// Returns raw Win32_Printer rows. Status field maps a numeric value to a PrinterStatus enum.
    /// </summary>
    public static List<Win32PrinterRow> QueryPrinters()
    {
        var result = new List<Win32PrinterRow>();
        foreach (ManagementObject mo in EnumerateInstances("Win32_Printer"))
        {
            result.Add(new Win32PrinterRow
            {
                Name                  = WmiStr(mo, "Name"),
                DeviceId              = WmiStr(mo, "DeviceID"),
                ShareName             = WmiStr(mo, "ShareName"),
                PortName              = WmiStr(mo, "PortName"),
                DriverName            = WmiStr(mo, "DriverName"),
                Manufacturer          = WmiStr(mo, "Manufacturer").Trim(),
                Description           = WmiStr(mo, "Description"),
                Status                = WmiStr(mo, "Status"),
                StatusInfo            = WmiUint(mo, "StatusInfo"),
                IsDefault             = WmiBool(mo, "Default"),
                IsNetwork             = WmiBool(mo, "Network"),
                IsLocal               = WmiBool(mo, "Local"),
                IsShared              = WmiBool(mo, "Shared"),
                Location              = WmiStr(mo, "Location"),
                Comment               = WmiStr(mo, "Comment"),
                WorkOffline           = WmiBool(mo, "WorkOffline"),
                DetectedErrorState    = WmiUint(mo, "DetectedErrorState"),
                PrinterStatusRaw      = WmiUint(mo, "PrinterStatus"),
                ErrorStateRaw         = WmiUint(mo, "ErrorState"),
                ExtendedPrinterStatus = WmiUint(mo, "ExtendedPrinterStatus")
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
        foreach (ManagementObject mo in EnumerateInstances("Win32_PrinterDriver"))
        {
            result.Add(new Win32PrinterDriverRow
            {
                Name             = WmiStr(mo, "Name"),
                FilePath         = WmiStr(mo, "FilePath"),
                InfName          = WmiStr(mo, "INFName"),
                DriverPath       = WmiStr(mo, "DriverPath"),
                ConfigFile       = WmiStr(mo, "ConfigFile"),
                HelpFile         = WmiStr(mo, "HelpFile"),
                MonitorName      = WmiStr(mo, "MonitorName"),
                DefaultDataType  = WmiStr(mo, "DefaultDataType"),
                StartMode        = WmiStr(mo, "StartMode"),
                SupportedPlatform = WmiStr(mo, "SupportedPlatform"),
                Version          = WmiStr(mo, "Version"),
                ManufacturerName = WmiStr(mo, "ManufacturerName")
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
        foreach (ManagementObject mo in EnumerateInstances("Win32_PnPSignedDriver"))
        {
            result.Add(new Win32PnPSignedDriverRow
            {
                DeviceName        = WmiStr(mo, "DeviceName"),
                DeviceClass       = WmiStr(mo, "DeviceClass"),
                DeviceID          = WmiStr(mo, "DeviceID"),
                DriverDate        = WmiStr(mo, "DriverDate"),
                DriverVersion     = WmiStr(mo, "DriverVersion"),
                DriverProviderName = WmiStr(mo, "DriverProviderName"),
                InfName           = WmiStr(mo, "InfName"),
                IsSigned          = WmiBool(mo, "IsSigned"),
                Signer            = WmiStr(mo, "Signer"),
                HardwareID        = WmiArr(mo, "HardwareID"),
                CompatibleID      = WmiArr(mo, "CompatibleID"),
                LocationPath      = WmiStr(mo, "LocationPath"),
                ClassGuid         = WmiStr(mo, "ClassGuid")
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
        foreach (ManagementObject mo in EnumerateInstances("Win32_PnPSignedDriver"))
        {
            var inf = WmiStr(mo, "InfName");
            if (!string.Equals(inf, infName, StringComparison.OrdinalIgnoreCase)) continue;
            var id = WmiStr(mo, "DeviceID");
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
        foreach (ManagementObject mo in EnumerateInstances("Win32_PnPEntity"))
        {
            var pnpClass = WmiStr(mo, "PNPClass");
            if (pnpClass != "Printer" && pnpClass != "USB") continue;
            var name = WmiStr(mo, "Name");
            var manufacturer = WmiStr(mo, "Manufacturer");
            var hwid = WmiArr(mo, "HardwareID");
            if (!IsHp(name, manufacturer, hwid)) continue;

            result.Add(new Win32PnPEntityRow
            {
                Name          = name,
                Caption       = WmiStr(mo, "Caption"),
                Description   = WmiStr(mo, "Description"),
                DeviceID      = WmiStr(mo, "DeviceID"),
                Manufacturer  = manufacturer,
                PnpClass      = pnpClass,
                ClassGuid     = WmiStr(mo, "PNPClassGuid"),
                Present       = WmiBool(mo, "Present"),
                Status        = WmiStr(mo, "Status"),
                HardwareID    = hwid,
                CompatibleID  = WmiArr(mo, "CompatibleID")
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
    /// <summary>WMI DeviceID. Stable across name changes; used to key per-printer settings.</summary>
    public string DeviceId { get; init; } = string.Empty;
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
