using System.Runtime.InteropServices;
using PrintHub.Core.Escl;

namespace PrintHub.Core.Scanning;

public sealed class WiaException : Exception
{
    public WiaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Windows Image Acquisition (late-bound COM automation). Covers USB scanners and WSD network scanners
/// that don't speak eSCL; it is also the path used by drivers that only ship a WIA minidriver.
/// </summary>
public static class WiaScanner
{
    const string FormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    const string FormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    const int PaperEmpty = unchecked((int)0x80210003);
    const int E_InvalidArg = unchecked((int)0x80070057), E_Fail = unchecked((int)0x80004005), WiaGeneralError = unchecked((int)0x80210001);

    // WIA property ids
    const int DocumentHandlingCapabilities = 3086, DocumentHandlingSelect = 3088, Pages = 3096, ItemName = 4098,
        CurrentIntent = 6146, HRes = 6147, VRes = 6148,
        HStart = 6149, VStart = 6150, HExtent = 6151, VExtent = 6152, DataType = 4103, BitsPerPixel = 4104;

    // Document handling flags (WIA_DPS_DOCUMENT_HANDLING_*)
    const int HandlingFeeder = 1, HandlingFlatbed = 2, HandlingDuplex = 4;

    static Task<T> Sta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    public static bool IsAvailable => Type.GetTypeFromProgID("WIA.DeviceManager") is not null;

    /// <summary>Synchronous list for discovery (spins up a short-lived STA thread).</summary>
    public static List<(string Id, string Name)> ListScanners()
    {
        if (!IsAvailable) return new();
        return Sta(() =>
        {
            var list = new List<(string, string)>();
            dynamic mgr = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager")!)!;
            int n = mgr.DeviceInfos.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic info = mgr.DeviceInfos[i];
                if ((int)info.Type != 1) continue; // 1 = scanner
                string name = info.Properties["Name"].Value;
                list.Add(((string)info.DeviceID, name));
            }
            return list;
        }).GetAwaiter().GetResult();
    }

    public static Task<List<byte[]>> ScanAsync(string deviceId, ScanSettings s, IProgress<int>? progress = null, CancellationToken ct = default) =>
        Sta(() => ScanCore(deviceId, s, progress, ct));

    /// <summary>
    /// Scanner drivers differ a lot in what they accept, and a rejected value shows up as "The parameter is incorrect" when the
    /// page is transferred. So: start with the settings the user asked for, set in the order WIA expects, and if the driver
    /// still refuses, retry from a clean connection with fewer and fewer settings before giving up.
    /// </summary>
    static List<byte[]> ScanCore(string deviceId, ScanSettings s, IProgress<int>? progress, CancellationToken ct)
    {
        COMException? firstFailure = null;
        for (int level = 0; level < 3; level++)
        {
            var pages = new List<byte[]>();
            try
            {
                ScanAttempt(deviceId, s, level, pages, progress, ct);
                if (pages.Count == 0) throw new WiaException(s.Source == ScanSource.Flatbed ? "The scanner returned no image." : "No pages were found in the document feeder.");
                return pages;
            }
            catch (COMException ex) when (IsEndOfFeed(ex) && pages.Count > 0 && s.Source != ScanSource.Flatbed) { return pages; }
            catch (COMException ex) when (IsDriverRejection(ex) && pages.Count == 0 && level < 2)
            {
                firstFailure ??= ex;
                Diag.Log($"WIA mode {level} was rejected by the driver (0x{(uint)ex.HResult:X8} {ex.Message}); retrying with fewer settings");
            }
            catch (COMException ex) { throw new WiaException(Describe(ex, level > 0 || firstFailure is not null), ex); }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { throw new WiaException("This scanner's WIA driver rejected the request: " + ex.Message, ex); }
        }
        throw new WiaException(Describe(firstFailure!, true), firstFailure);
    }

    // Some drivers report "no more paper" as 0x80210003; a few report it as an invalid parameter or a general error on the extra call.
    static bool IsEndOfFeed(COMException ex) => ex.HResult == PaperEmpty || ex.HResult == E_InvalidArg || ex.HResult == WiaGeneralError;
    static bool IsDriverRejection(COMException ex) => ex.HResult == E_InvalidArg || ex.HResult == WiaGeneralError || ex.HResult == E_Fail;

    static void ScanAttempt(string deviceId, ScanSettings s, int level, List<byte[]> pages, IProgress<int>? progress, CancellationToken ct)
    {
        dynamic mgr = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager")!)!;
        dynamic? dev = null;
        int n = mgr.DeviceInfos.Count;
        for (int i = 1; i <= n && dev is null; i++)
        {
            dynamic info = mgr.DeviceInfos[i];
            if ((string)info.DeviceID == deviceId) dev = info.Connect();
        }
        if (dev is null) throw new WiaException("The scanner is not connected.");

        bool feeder = s.Source != ScanSource.Flatbed;
        bool duplex = s.Source == ScanSource.FeederDuplex;
        int? caps = GetInt(dev.Properties, DocumentHandlingCapabilities);
        if (duplex && caps is { } c && (c & HandlingDuplex) == 0) { Diag.Log("WIA: the driver offers no duplex feeder; scanning one side"); duplex = false; }
        int handling = feeder ? HandlingFeeder | (duplex ? HandlingDuplex : 0) : HandlingFlatbed;
        Diag.Log($"WIA scan: mode {level}, source {s.Source}, {s.Dpi} dpi, {s.Color}, handling flags 0x{handling:X} (driver offers {(caps is { } cv ? "0x" + cv.ToString("X") : "unknown")})");

        // WIA 2.0 drivers expose the flatbed and the feeder as separate child items; scan from the one that matches.
        dynamic item = PickItem(dev, feeder);

        // The source is chosen on the device and, for WIA 2.0 drivers, on the item as well.
        TrySet(dev.Properties, DocumentHandlingSelect, handling);
        TrySet(item.Properties, DocumentHandlingSelect, handling);
        bool pageByPage = level >= 1;
        if (feeder && !TrySet(item.Properties, Pages, pageByPage ? 1 : 0)) TrySet(dev.Properties, Pages, pageByPage ? 1 : 0); // 0 = all pages

        void Configure(dynamic it)
        {
            var p = it.Properties;
            if (level >= 2) { TrySet(p, VRes, s.Dpi); TrySet(p, HRes, s.Dpi); return; } // bare minimum: leave everything else to the driver

            // Intent first: choosing it resets resolution and format to the driver's defaults, so the specific values come after it.
            TrySet(p, CurrentIntent, s.Color == ScanColor.Color ? 1 : 2);
            if (s.Color == ScanColor.Color) { TrySet(p, DataType, 3); TrySet(p, BitsPerPixel, 24); }
            else { TrySet(p, DataType, 2); TrySet(p, BitsPerPixel, 8); } // B&W is done later by the filter
            TrySet(p, HRes, s.Dpi); TrySet(p, VRes, s.Dpi);
            int dpi = GetInt(p, HRes) ?? s.Dpi;
            TrySet(p, HStart, 0); TrySet(p, VStart, 0);
            if (!s.Paper.IsAuto)
            {
                TrySet(p, HExtent, (int)(s.Paper.WidthIn * dpi));
                TrySet(p, VExtent, (int)(s.Paper.HeightIn * dpi));
            }
            else if (level >= 1)
            {
                // some feeder drivers keep the flatbed's size from before and reject it: ask for the largest area they allow
                if (MaxOf(p, HExtent) is { } mh) TrySet(p, HExtent, mh);
                if (MaxOf(p, VExtent) is { } mv) TrySet(p, VExtent, mv);
            }
        }
        Configure(item);

        do
        {
            ct.ThrowIfCancellationRequested();
            byte[]? data = TransferOne(item);
            if (data is null) break;
            pages.Add(data);
            progress?.Report(pages.Count);
            if (feeder && pageByPage) { item = PickItem(dev, feeder); TrySet(item.Properties, DocumentHandlingSelect, handling); Configure(item); } // some drivers reset after every page
        } while (feeder);
    }

    static dynamic PickItem(dynamic dev, bool feeder)
    {
        int count = dev.Items.Count;
        if (count < 1) throw new WiaException("The scanner driver exposes nothing to scan from.");
        string[] wanted = feeder ? new[] { "feeder", "adf", "document" } : new[] { "flatbed", "platen", "glass" };
        for (int i = 1; i <= count; i++)
        {
            dynamic it = dev.Items[i];
            var name = (GetString(it.Properties, ItemName) ?? "").ToLowerInvariant();
            if (wanted.Any(w => name.Contains(w))) { Diag.Log($"WIA item: '{name}' ({i} of {count})"); return it; }
        }
        Diag.Log($"WIA item: no child named like '{wanted[0]}'; using the first of {count}");
        return dev.Items[1];
    }

    static byte[]? TransferOne(dynamic item)
    {
        COMException? first = null;
        foreach (var format in new[] { FormatJpeg, FormatBmp, FormatPng })
        {
            try
            {
                dynamic img = item.Transfer(format);
                string ext = img.FileExtension;
                string path = Path.Combine(Path.GetTempPath(), $"printhub-{Guid.NewGuid():N}.{ext}");
                try { img.SaveFile(path); return File.ReadAllBytes(path); }
                finally { try { File.Delete(path); } catch { } }
            }
            catch (COMException ex) when (ex.HResult == PaperEmpty) { return null; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80210006) || ex.HResult == unchecked((int)0x80210005)) { throw; } // busy / offline: another format will not help
            catch (COMException ex) { first ??= ex; Diag.Log($"WIA transfer as {format} failed: 0x{(uint)ex.HResult:X8} {ex.Message}"); }
        }
        throw first!;
    }

    // ---- property helpers ----

    static dynamic? FindProp(dynamic props, int id)
    {
        try { foreach (dynamic prop in props) if ((int)prop.PropertyID == id) return prop; } catch { }
        return null;
    }

    static int? GetInt(dynamic props, int id)
    {
        try { var p = FindProp(props, id); return p is null ? null : (int?)Convert.ToInt32(p.Value); } catch { return null; }
    }

    static string? GetString(dynamic props, int id)
    {
        try { var p = FindProp(props, id); return p is null ? null : (string?)Convert.ToString(p.Value); } catch { return null; }
    }

    static int? MaxOf(dynamic props, int id)
    {
        try
        {
            var p = FindProp(props, id);
            if (p is null || (int)p.SubType != 1) return null; // 1 = range
            return (int)p.SubTypeMax;
        }
        catch { return null; }
    }

    /// <summary>Set a property to the closest value the driver says it accepts. Returns false when the driver has no such property or refuses it.</summary>
    static bool TrySet(dynamic props, int id, int value)
    {
        try
        {
            var prop = FindProp(props, id);
            if (prop is null) return false;
            prop.Value = Nearest(prop, value);
            return true;
        }
        catch (Exception ex) { Diag.Log($"WIA: property {id} = {value} refused: {ex.Message}"); return false; }
    }

    static int Nearest(dynamic prop, int value)
    {
        try
        {
            switch ((int)prop.SubType)
            {
                case 1: // range
                {
                    int min = prop.SubTypeMin, max = prop.SubTypeMax, step = Math.Max(1, (int)prop.SubTypeStep);
                    int v = Math.Clamp(value, min, max);
                    return min + (int)Math.Round((v - min) / (double)step) * step;
                }
                case 2: // list
                {
                    dynamic vals = prop.SubTypeValues;
                    int count = vals.Count, best = value, bestDiff = int.MaxValue;
                    for (int i = 1; i <= count; i++)
                    {
                        int candidate = Convert.ToInt32(vals[i]);
                        if (Math.Abs(candidate - value) < bestDiff) { best = candidate; bestDiff = Math.Abs(candidate - value); }
                    }
                    return best;
                }
            }
        }
        catch { /* no usable limits: send the value as it is */ }
        return value;
    }

    const string Fallback = " HSA tried again with fewer settings and the driver still refused. If the printer is also on your network, choose it by its network entry so HSA can scan with eSCL instead; otherwise check that the manufacturer's scan app can use the feeder.";

    static string Describe(COMException ex, bool triedFallbacks = false) => (uint)ex.HResult switch
    {
        0x80210001 => "The scanner reported an unknown error." + (triedFallbacks ? Fallback : ""),
        0x80210002 => "Paper jam. Clear the paper path and try again.",
        0x80210003 => "The document feeder is empty.",
        0x80210004 => "The scanner could not be found.",
        0x80210005 => "The scanner is offline. Check the cable or network connection and that it is switched on.",
        0x80210006 => "The scanner is busy. Wait for the current operation to finish.",
        0x80210007 => "The scanner cover is open.",
        0x80210008 => "The scanner is locked by another program.",
        0x80210015 => "No scanner was found. Make sure it is connected and turned on.",
        0x80210016 => "The scanner cover is open.",
        0x80070057 => "The scanner's Windows driver rejected the scan settings (\"the parameter is incorrect\")." + (triedFallbacks ? Fallback : ""),
        _ => $"WIA error 0x{(uint)ex.HResult:X8}: {ex.Message}",
    };
}
