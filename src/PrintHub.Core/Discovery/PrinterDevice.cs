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
    /// <summary>The Windows queue is switched to "use printer offline" or reports the printer offline.</summary>
    public bool SpoolerOffline { get; set; }
    /// <summary>Plugged in by USB at the moment of the search (not just a queue left over from earlier). Set by <see cref="PrinterPicker.MarkUsb"/>.</summary>
    public bool OnUsb { get; set; }

    /// <summary>
    /// The container id of the physical USB printer this entry stands for (it is the same for everything one printer plugs in: its queue's port, its print interface,
    /// its web-services interface). It is what ties a queue to the right USB interface when many printers of one model are around. Null when not known.
    /// </summary>
    public string? UsbContainer { get; set; }

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
    /// <summary>True for Brother printers (by maker, name, Windows driver or USB vendor id). Gates the Brother-only maintenance tools.</summary>
    public bool IsBrother => Manufacturer.Contains("brother", StringComparison.OrdinalIgnoreCase) || Name.Contains("brother", StringComparison.OrdinalIgnoreCase)
                             || (SpoolerDriver?.Contains("brother", StringComparison.OrdinalIgnoreCase) ?? false) || UsbCandidates.Any(u => u.VendorId == 0x04F9);
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

    /// <summary>
    /// The mark Windows adds to a second queue of the same printer: "EPSON L3250 Series (Copy 1)". (Also "(Kopie 1)", "(Copie 2)", "(redirected 3)": a trailing word and a number in brackets.)
    /// It says nothing about the printer. It used to count as a shared word, so an Epson "(Copy 1)" matched an HP "(Copy 1)" and the new Epson turned up under the HP's name.
    /// </summary>
    static readonly Regex QueueSuffixRx = new(@"\s*\(\s*\p{L}+\s+\d+\s*\)\s*$", RegexOptions.Compiled);

    public static HashSet<string> Tokens(string s) =>
        Regex.Split(QueueSuffixRx.Replace(s, "").ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0 && !Stop.Contains(t))
            .SelectMany(SplitJoinedModels)
            .Select(t => Aliases.TryGetValue(t, out var full) ? full : t).ToHashSet();

    /// <summary>"m282m285" (two model numbers written together, as in "HP LJ M282M285") also counts as "m282" and "m285".</summary>
    static IEnumerable<string> SplitJoinedModels(string t)
    {
        yield return t;
        var parts = Regex.Matches(t, "[a-z]+[0-9]+");
        if (parts.Count >= 2 && string.Concat(parts.Select(m => m.Value)) == t) foreach (Match m in parts) yield return m.Value;
    }

    public static bool Similar(string a, string b)
    {
        var ta = Tokens(a); var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return false;
        var common = ta.Intersect(tb).ToList();
        // a model carries a digit-bearing word of its own ("l3250", "580", "m282"); a lone "1" or "2" is a copy number or a count, not a model
        if (common.Count == 0 || !common.Any(t => t.Length >= 2 && t.Any(char.IsDigit))) return false;
        if (common.Count >= Math.Min(ta.Count, tb.Count) * 0.6) return true;

        // Same model number (at least three digits, e.g. 1110 or 16650) and not two different brands: one physical printer
        // that the queue, the USB descriptor and the scanner driver simply spell differently.
        string? brandA = BrandRx.Match(a).Value.ToLowerInvariant(), brandB = BrandRx.Match(b).Value.ToLowerInvariant();
        if (brandA == "hewlett") brandA = "hp"; if (brandB == "hewlett") brandB = "hp";
        bool brandsDiffer = brandA.Length > 0 && brandB.Length > 0 && brandA != brandB;
        return !brandsDiffer && common.Any(t => t.Count(char.IsDigit) >= 3);
    }

    /// <summary>
    /// The entry of <paramref name="list"/> that is the same printer as <paramref name="target"/>: same id, else the same name, and only then a similar
    /// name. The exact name comes first so that two printers of one model (two L3250s) are never mistaken for each other.
    /// </summary>
    public static PrinterDevice? FindSame(IEnumerable<PrinterDevice> list, PrinterDevice target)
    {
        var all = list as IReadOnlyCollection<PrinterDevice> ?? list.ToList();
        return all.FirstOrDefault(d => d.Id == target.Id) ?? all.FirstOrDefault(d => d.Name == target.Name) ?? all.FirstOrDefault(d => Similar(d.Name, target.Name));
    }

    /// <summary>Fold another discovery record of the same physical device into this one.</summary>
    public void Merge(PrinterDevice o)
    {
        if (Name.Length < o.Name.Length && IppUri is null) Name = o.Name;
        if (Manufacturer == "") Manufacturer = o.Manufacturer;
        if (Model == "") Model = o.Model;
        Address ??= o.Address;
        IppUri ??= o.IppUri; EsclUri ??= o.EsclUri; WebUri ??= o.WebUri;
        if (SpoolerName is null) { SpoolerName = o.SpoolerName; SpoolerPort = o.SpoolerPort; SpoolerDriver = o.SpoolerDriver; SpoolerStatus = o.SpoolerStatus; SpoolerOffline = o.SpoolerOffline; }
        if (Usb is null) Usb = o.Usb;
        foreach (var u in o.UsbCandidates) if (!UsbCandidates.Any(x => x.InstanceId == u.InstanceId)) UsbCandidates.Add(u);
        WiaDeviceId ??= o.WiaDeviceId;
    }
}
