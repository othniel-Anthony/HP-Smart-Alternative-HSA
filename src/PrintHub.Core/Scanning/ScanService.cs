using System.Runtime.CompilerServices;
using PrintHub.Core.Discovery;
using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;

namespace PrintHub.Core.Scanning;

/// <summary>Runs a scan on whichever backend the printer supports (eSCL first, WIA as fallback) and applies the post-processing options.</summary>
public static class ScanService
{
    public static async Task<EsclCapabilities?> GetCapabilitiesAsync(PrinterSession? session, CancellationToken ct = default)
    {
        if (session?.Escl is null) return null;
        try { return await session.Escl.GetCapabilitiesAsync(ct); } catch { return null; }
    }

    public static async IAsyncEnumerable<ScannedPage> ScanAsync(PrinterSession? session, PrinterDevice device, ScanSettings s,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (session?.Escl is { } escl)
        {
            EsclCapabilities? caps = null;
            string? esclFailure = null;
            try { caps = await escl.GetCapabilitiesAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException) { esclFailure = ex.Message; }

            if (caps is not null)
            {
                var src = caps.Caps(s.Source == ScanSource.FeederDuplex ? ScanSource.Feeder : s.Source)
                    ?? throw new EsclException(s.Source == ScanSource.Flatbed ? "This scanner has no flatbed." : "This scanner has no document feeder.");
                if (s.Source == ScanSource.FeederDuplex && !caps.FeederDuplex) s = With(s, x => x.Source = ScanSource.Feeder);

                var req = new EsclScanRequest
                {
                    Source = s.Source, Color = s.Color, Dpi = Nearest(src.Resolutions, s.Dpi),
                    WidthUnits = s.Paper.IsAuto ? 0 : (int)(s.Paper.WidthIn * 300), HeightUnits = s.Paper.IsAuto ? 0 : (int)(s.Paper.HeightIn * 300),
                };
                // If the eSCL scan fails before any page arrives and the printer also has a Windows scanner driver, use that instead.
                int delivered = 0;
                Exception? esclError = null;
                await using (var stream = escl.ScanAsync(req, src, ct).GetAsyncEnumerator(ct))
                {
                    while (true)
                    {
                        bool has;
                        try { has = await stream.MoveNextAsync(); }
                        catch (Exception ex) when (delivered == 0 && device.WiaDeviceId is not null && !ct.IsCancellationRequested
                                                   && ex is EsclException or HttpRequestException or IOException or TimeoutException)
                        { esclError = ex; break; }
                        if (!has) break;
                        delivered++;
                        yield return await PostProcess(stream.Current, s, req.Dpi);
                    }
                }
                if (esclError is null) yield break;
                Diag.Log($"eSCL scan failed ({esclError.Message}); using the Windows scanner driver instead");
            }
            if (device.WiaDeviceId is null) throw new EsclException("The scanner did not respond: " + esclFailure);
        }

        if (device.WiaDeviceId is null) throw new InvalidOperationException("No scanning interface is available for this printer. Connect it over the network or USB.");
        var pages = await WiaScanner.ScanAsync(device.WiaDeviceId, s, null, ct);
        foreach (var bytes in pages) yield return await PostProcess(bytes, s, s.Dpi);
    }

    static ScanSettings With(ScanSettings s, Action<ScanSettings> f) { var c = s.Clone(); f(c); return c; }

    static int Nearest(List<int> available, int wanted) =>
        available.Count == 0 ? wanted : available.OrderBy(a => Math.Abs(a - wanted)).First();

    /// <summary>Apply the scan options as non-destructive edits (original bytes stay untouched).</summary>
    static Task<ScannedPage> PostProcess(byte[] bytes, ScanSettings s, int dpi) => Task.Run(() =>
    {
        var page = new ScannedPage(bytes, dpi);
        using var bmp = ImageTools.Load(bytes);

        if (s.AutoStraighten) { var a = ImageTools.DetectDocumentSkew(bmp); if (Math.Abs(a) >= 0.3) page.Edits.Straighten = -a; }
        if (s.AutoCrop && s.Paper.IsAuto && s.Source == ScanSource.Flatbed)
            if (ImageTools.DetectDocumentBounds(page.Edits.Straighten == 0 ? bmp : ImageTools.RotateArbitrary(bmp, page.Edits.Straighten)) is { } r)
                page.Edits.Crop = r;

        page.Edits.Filter = s.Color switch
        {
            ScanColor.BlackAndWhite => PageFilter.BlackAndWhite,
            ScanColor.Grayscale => PageFilter.Grayscale,
            _ => s.Enhance ? PageFilter.Enhance : PageFilter.Original,
        };
        return page;
    });
}
