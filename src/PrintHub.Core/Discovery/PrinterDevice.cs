using System.Text.RegularExpressions;
using PrintHub.Core.Usb;

namespace PrintHub.Core.Discovery;

/// <summary>One physical printer/MFP, merged from every way it can be reached (network, Windows queue, USB, WIA).</summary>
public sealed class PrinterDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Address { get; set; }

    public Uri? IppUri { get; set; }
    public Uri? EsclUri { get; set; }
    public Uri? WebUri { get; set; }

    /// <summary>Windows print queue name (driver based printing, printing preferences, test page).</summary>
    public string? SpoolerName { get; set; }
    public string? SpoolerPort { get; set; }
    public string? SpoolerDriver { get; set; }
    public uint SpoolerStatus { get; set; }

    public UsbInterfaceInfo? Usb { get; set; }
    public List<UsbInterfaceInfo> UsbCandidates { get; set; } = new();
    public string? WiaDeviceId { get; set; }

    public bool IsHp => Manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase) || Manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase)
                        || Name.StartsWith("HP ", StringComparison.OrdinalIgnoreCase) || UsbCandidates.Any(u => u.VendorId == 0x03F0);
    /// <summary>True for Epson printers (by maker, name, Windows driver or USB vendor id). Gates the Epson-only maintenance tools.</summary>
    public bool IsEpson => Manufacturer.Contains("epson", StringComparison.OrdinalIgnoreCase) || Name.Contains("epson", StringComparison.OrdinalIgnoreCase)
                           || (SpoolerDriver?.Contains("epson", StringComparison.OrdinalIgnoreCase) ?? false) || UsbCandidates.Any(u => u.VendorId == 0x04B8);
    /// <summary>True for Canon printers (by maker, name or Windows driver). Gates the Canon-only maintenance tools.</summary>
    public bool IsCanon => Manufacturer.Contains("canon", StringComparison.OrdinalIgnoreCase) || Name.Contains("canon", StringComparison.OrdinalIgnoreCase)
                           || Name.StartsWith("PIXMA", StringComparison.OrdinalIgnoreCase) || (SpoolerDriver?.Contains("canon", StringComparison.OrdinalIgnoreCase) ?? false);
    public bool HasNetwork => IppUri is not null || EsclUri is not null;
    public bool HasUsbHttp => Usb is { Openable: true } || UsbCandidates.Any(u => u.Openable);
    public bool CanScan => EsclUri is not null || WiaDeviceId is not null || HasUsbHttp;
    public bool CanPrint => SpoolerName is not null || IppUri is not null || HasUsbHttp;

    public string Connection => string.Join(" + ", new[]
    {
        HasNetwork ? "Network" : null,
        HasUsbHttp ? "USB" : null,
        SpoolerName is not null && !HasNetwork && !HasUsbHttp ? "Windows" : null,
        WiaDeviceId is not null && !HasNetwork && !HasUsbHttp && SpoolerName is null ? "Scanner only" : null,
    }.Where(s => s is not null));

    public override string ToString() => Name;

    // ---- fuzzy matching between different discovery sources ----

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "hp", "hewlett", "packard", "epson", "canon", "brother", "series", "printer", "all", "in", "one", "usb", "net",
        "network", "ipp", "driver", "class", "pcl", "ps", "xl", "wsd", "scanner", "mfp", "bluetooth", "esc", "p", "r", "v4", "universal", "printing",
    };

    // Short forms that drivers and USB descriptors use for the same product line ("HP DJ 1110 series" is the "DeskJet 1110 series").
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dj"] = "deskjet", ["oj"] = "officejet", ["ojp"] = "officejet", ["lj"] = "laserjet", ["pw"] = "pagewide",
    };

    static readonly Regex BrandRx = new(@"\b(hp|hewlett|epson|canon|brother|lexmark|samsung|xerox|ricoh|kyocera|oki|konica|dell|sharp|pantum)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static HashSet<string> Tokens(string s) =>
        Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0 && !Stop.Contains(t))
            .Select(t => Aliases.TryGetValue(t, out var full) ? full : t).ToHashSet();

    public static bool Similar(string a, string b)
    {
        var ta = Tokens(a); var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return false;
        var common = ta.Intersect(tb).ToList();
        if (common.Count == 0 || !common.Any(t => t.Any(char.IsDigit))) return false;
        if (common.Count >= Math.Min(ta.Count, tb.Count) * 0.6) return true;

        // Same model number (at least three digits, e.g. 1110 or 16650) and not two different brands: one physical printer
        // that the queue, the USB descriptor and the scanner driver simply spell differently.
        string? brandA = BrandRx.Match(a).Value.ToLowerInvariant(), brandB = BrandRx.Match(b).Value.ToLowerInvariant();
        if (brandA == "hewlett") brandA = "hp"; if (brandB == "hewlett") brandB = "hp";
        bool brandsDiffer = brandA.Length > 0 && brandB.Length > 0 && brandA != brandB;
        return !brandsDiffer && common.Any(t => t.Count(char.IsDigit) >= 3);
    }

    /// <summary>Fold another discovery record of the same physical device into this one.</summary>
    public void Merge(PrinterDevice o)
    {
        if (Name.Length < o.Name.Length && IppUri is null) Name = o.Name;
        if (Manufacturer == "") Manufacturer = o.Manufacturer;
        if (Model == "") Model = o.Model;
        Address ??= o.Address;
        IppUri ??= o.IppUri; EsclUri ??= o.EsclUri; WebUri ??= o.WebUri;
        if (SpoolerName is null) { SpoolerName = o.SpoolerName; SpoolerPort = o.SpoolerPort; SpoolerDriver = o.SpoolerDriver; SpoolerStatus = o.SpoolerStatus; }
        if (Usb is null) Usb = o.Usb;
        foreach (var u in o.UsbCandidates) if (!UsbCandidates.Any(x => x.InstanceId == u.InstanceId)) UsbCandidates.Add(u);
        WiaDeviceId ??= o.WiaDeviceId;
    }
}
