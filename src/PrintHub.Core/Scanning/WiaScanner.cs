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
    const int PaperEmpty = unchecked((int)0x80210003);

    // WIA property ids
    const int DocumentHandlingSelect = 3088, Pages = 3096, CurrentIntent = 6146, HRes = 6147, VRes = 6148,
        HStart = 6149, VStart = 6150, HExtent = 6151, VExtent = 6152, Brightness = 6154, Contrast = 6155, DataType = 4103, BitsPerPixel = 4104;

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

    static List<byte[]> ScanCore(string deviceId, ScanSettings s, IProgress<int>? progress, CancellationToken ct)
    {
        var pages = new List<byte[]>();
        try
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
            SetProp(dev.Properties, DocumentHandlingSelect, feeder ? (s.Source == ScanSource.FeederDuplex ? 5 : 1) : 2);
            if (feeder) SetProp(dev.Properties, Pages, 0); // 0 = all pages

            dynamic item = dev.Items[1];
            var p = item.Properties;
            SetProp(p, HRes, s.Dpi); SetProp(p, VRes, s.Dpi);
            switch (s.Color)
            {
                case ScanColor.Color: SetProp(p, CurrentIntent, 1); SetProp(p, DataType, 3); SetProp(p, BitsPerPixel, 24); break;
                default: SetProp(p, CurrentIntent, 2); SetProp(p, DataType, 2); SetProp(p, BitsPerPixel, 8); break; // B&W is done later by the filter
            }
            SetProp(p, HStart, 0); SetProp(p, VStart, 0);
            if (!s.Paper.IsAuto)
            {
                SetProp(p, HExtent, (int)(s.Paper.WidthIn * s.Dpi));
                SetProp(p, VExtent, (int)(s.Paper.HeightIn * s.Dpi));
            }

            do
            {
                ct.ThrowIfCancellationRequested();
                byte[]? data = TransferOne(item);
                if (data is null) break;
                pages.Add(data);
                progress?.Report(pages.Count);
            } while (feeder);
        }
        catch (COMException ex) when (ex.HResult == PaperEmpty && pages.Count > 0) { }
        catch (COMException ex) { throw new WiaException(Describe(ex), ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { throw new WiaException("This scanner's WIA driver rejected the request: " + ex.Message, ex); }

        if (pages.Count == 0) throw new WiaException(s.Source == ScanSource.Flatbed ? "The scanner returned no image." : "No pages were found in the document feeder.");
        return pages;
    }

    static byte[]? TransferOne(dynamic item)
    {
        dynamic img;
        try { img = item.Transfer(FormatJpeg); }
        catch (COMException ex) when (ex.HResult == PaperEmpty) { return null; }
        catch (COMException ex) when (ex.HResult != unchecked((int)0x80210006) && ex.HResult != unchecked((int)0x80210005))
        {
            try { img = item.Transfer(FormatBmp); }
            catch (COMException ex2) when (ex2.HResult == PaperEmpty) { return null; }
        }
        string ext = img.FileExtension;
        string path = Path.Combine(Path.GetTempPath(), $"printhub-{Guid.NewGuid():N}.{ext}");
        try { img.SaveFile(path); return File.ReadAllBytes(path); }
        finally { try { File.Delete(path); } catch { } }
    }

    static void SetProp(dynamic props, int id, object value)
    {
        try
        {
            foreach (dynamic prop in props)
                if ((int)prop.PropertyID == id) { prop.Value = value; return; }
        }
        catch { /* property not supported by this scanner: leave the driver default */ }
    }

    static string Describe(COMException ex) => (uint)ex.HResult switch
    {
        0x80210001 => "The scanner reported an unknown error.",
        0x80210002 => "Paper jam. Clear the paper path and try again.",
        0x80210003 => "The document feeder is empty.",
        0x80210004 => "The scanner could not be found.",
        0x80210005 => "The scanner is offline. Check the cable or network connection and that it is switched on.",
        0x80210006 => "The scanner is busy. Wait for the current operation to finish.",
        0x80210007 => "The scanner cover is open.",
        0x80210008 => "The scanner is locked by another program.",
        0x80210015 => "No scanner was found. Make sure it is connected and turned on.",
        0x80210016 => "The scanner cover is open.",
        _ => $"WIA error 0x{(uint)ex.HResult:X8}: {ex.Message}",
    };
}
