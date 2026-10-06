using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PrintHub.Core.Usb;

/// <summary>
/// Enumerates USB interfaces (including remembered, currently unplugged ones) from the PnP registry and
/// classifies the ones that carry HTTP (IPP-USB or HP web services), which is where the embedded web server lives.
/// </summary>
public static partial class UsbDeviceScanner
{
    const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\USB";

    [GeneratedRegex(@"Class_([0-9a-f]{2})&SubClass_([0-9a-f]{2})&Prot_([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ClassRx();
    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&MI_([0-9A-F]{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex IdRx();

    public static IReadOnlyList<UsbInterfaceInfo> Scan(bool presentOnly = true, int? vendorId = null)
    {
        var result = new List<UsbInterfaceInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(EnumRoot);
        if (root is null) return result;

        foreach (var devKeyName in root.GetSubKeyNames())
        {
            var m = IdRx().Match(devKeyName);
            if (!m.Success) continue;
            int vid = Convert.ToInt32(m.Groups[1].Value, 16), pid = Convert.ToInt32(m.Groups[2].Value, 16);
            if (vendorId is not null && vid != vendorId) continue;
            int? mi = m.Groups[3].Success ? Convert.ToInt32(m.Groups[3].Value, 16) : null;

            using var devKey = root.OpenSubKey(devKeyName);
            if (devKey is null) continue;
            foreach (var inst in devKey.GetSubKeyNames())
            {
                using var k = devKey.OpenSubKey(inst);
                if (k is null) continue;
                var instanceId = $@"USB\{devKeyName}\{inst}";
                var compat = (k.GetValue("CompatibleIDs") as string[]) ?? Array.Empty<string>();
                var cm = compat.Select(c => ClassRx().Match(c)).FirstOrDefault(x => x.Success);
                if (cm is null) continue;
                byte cls = Convert.ToByte(cm.Groups[1].Value, 16), sub = Convert.ToByte(cm.Groups[2].Value, 16), prot = Convert.ToByte(cm.Groups[3].Value, 16);

                var kind = (cls, sub, prot) switch
                {
                    (0x07, 0x01, 0x04) => UsbHttpKind.IppUsb,
                    (0xFF, 0x04, 0x01) when vid == 0x03F0 => UsbHttpKind.HpWebServices,
                    _ => UsbHttpKind.None,
                };

                // CM_Locate_DevNode without the PHANTOM flag only succeeds for devices that are currently present.
                bool present = NativeMethods.CM_Locate_DevNodeW(out _, instanceId, 0) == 0;
                if (presentOnly && !present) continue;

                var service = k.GetValue("Service") as string ?? "";
                var desc = k.GetValue("FriendlyName") as string ?? k.GetValue("DeviceDesc") as string ?? devKeyName;
                desc = desc[(desc.LastIndexOf(';') + 1)..];
                var container = k.GetValue("ContainerID") as string;

                string? path = null;
                if (present && service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
                    path = FindInterfacePath(k, instanceId);
                // The web-services interface of many HP printers is driven by Windows' scanner driver instead; it exposes its own device file.
                else if (present && kind != UsbHttpKind.None && service.Equals("usbscan", StringComparison.OrdinalIgnoreCase))
                    path = FindImagePath(instanceId);

                result.Add(new UsbInterfaceInfo(instanceId, vid, pid, mi, desc, service, cls, sub, prot, kind, present, path, container));
            }
        }
        return result;
    }

    /// <summary>Interfaces that should expose an embedded web server over USB.</summary>
    public static IReadOnlyList<UsbInterfaceInfo> FindHttpInterfaces(bool presentOnly = true) =>
        Scan(presentOnly).Where(i => i.HttpKind != UsbHttpKind.None).ToList();

    static string? FindImagePath(string instanceId)
    {
        var guid = UsbscanStream.ImageInterface;
        if (NativeMethods.CM_Get_Device_Interface_List_SizeW(out var len, ref guid, instanceId, 0) != 0 || len <= 1) return null;
        var buf = new char[len];
        if (NativeMethods.CM_Get_Device_Interface_ListW(ref guid, instanceId, buf, len, 0) != 0) return null;
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    static string? FindInterfacePath(RegistryKey instanceKey, string instanceId)
    {
        using var p = instanceKey.OpenSubKey("Device Parameters");
        var guids = p?.GetValue("DeviceInterfaceGUIDs") switch
        {
            string[] a => a,
            string s => new[] { s },
            _ => p?.GetValue("DeviceInterfaceGUID") is string g ? new[] { g } : Array.Empty<string>(),
        };
        foreach (var g in guids)
        {
            if (!Guid.TryParse(g.Trim('{', '}'), out var guid)) continue;
            if (NativeMethods.CM_Get_Device_Interface_List_SizeW(out var len, ref guid, instanceId, 0) != 0 || len <= 1) continue;
            var buf = new char[len];
            if (NativeMethods.CM_Get_Device_Interface_ListW(ref guid, instanceId, buf, len, 0) != 0) continue;
            var first = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null) return first;
        }
        return null;
    }
}
