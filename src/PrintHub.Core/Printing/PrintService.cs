using System.Globalization;
using PrintHub.Core.Discovery;
using PrintHub.Core.Imaging;
using PrintHub.Core.Ipp;

namespace PrintHub.Core.Printing;

/// <summary>Chooses between the Windows driver queue and direct IPP, and prepares the document for each.</summary>
public static class PrintService
{
    public static PrintRoute Resolve(PrinterDevice dev, PrinterSession? session, PrintRoute requested)
    {
        bool windows = dev.SpoolerName is not null;
        bool ipp = session?.Ipp is not null;
        switch (requested)
        {
            case PrintRoute.WindowsDriver when !windows: throw new InvalidOperationException("This printer has no Windows print queue. Add the printer in Windows Settings, or use direct IPP printing.");
            case PrintRoute.DirectIpp when !ipp: throw new InvalidOperationException("No IPP connection to this printer. Connect over the network or USB first.");
            case PrintRoute.WindowsDriver or PrintRoute.DirectIpp: return requested;
        }
        if (windows) return PrintRoute.WindowsDriver;
        if (ipp) return PrintRoute.DirectIpp;
        throw new InvalidOperationException("This printer cannot print from here yet: it has no Windows queue and no IPP connection.");
    }

    public static async Task PrintFileAsync(PrinterDevice dev, PrinterSession? session, string path, PrintOptions o, CancellationToken ct = default)
    {
        var src = await PrintSource.FromFileAsync(path, ct);
        await PrintAsync(dev, session, src, o, ct);
    }

    public static async Task PrintAsync(PrinterDevice dev, PrinterSession? session, PrintSource src, PrintOptions o, CancellationToken ct = default)
    {
        var route = Resolve(dev, session, o.Route);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Over a USB cable the Windows driver has to turn the PDF into a huge raster image and push it through the cable, which can
        // take a minute. A printer that has an IPP-over-USB session and takes PDF directly gets the file itself instead (what HP Smart
        // does). If it refuses, nothing was printed and the driver route below still runs.
        if (o.Route == PrintRoute.Auto && route == PrintRoute.WindowsDriver && session is { ViaUsb: true, Ipp: { } usbIpp }
            && src.OriginalMime == "application/pdf" && string.IsNullOrEmpty(o.PrintToFilePath))
        {
            bool takesPdf = false;
            try { takesPdf = (await usbIpp.GetStatusAsync(ct)).SupportsPdf; } catch (Exception ex) when (ex is not OperationCanceledException) { Diag.Log("Print: could not ask the printer about PDF support over USB: " + ex.Message); }
            if (takesPdf)
            {
                try
                {
                    await PrintViaIppAsync(usbIpp, src, o, ct);
                    Diag.Log($"Print: PDF sent straight to the printer over USB in {sw.ElapsedMilliseconds} ms");
                    return;
                }
                catch (IppException ex) { Diag.Log($"Print: the printer refused the PDF over USB ({ex.Message}); using the Windows driver"); }
            }
        }

        if (route == PrintRoute.WindowsDriver) await WindowsPrintService.PrintAsync(dev.SpoolerName!, src, o, src.Name, ct);
        else await PrintViaIppAsync(session!.Ipp!, src, o, ct);
        Diag.Log($"Print: {route} route finished in {sw.ElapsedMilliseconds} ms");
    }

    static async Task PrintViaIppAsync(IppClient ipp, PrintSource src, PrintOptions o, CancellationToken ct)
    {
        var status = await ipp.GetStatusAsync(ct);
        var opts = new IppPrintOptions
        {
            Copies = Math.Max(1, o.Copies),
            Color = o.Color && (status.ColorSupported || status.ColorModes.Contains("color")),
            Sides = o.Duplex switch { DuplexMode.LongEdge => "two-sided-long-edge", DuplexMode.ShortEdge => "two-sided-short-edge", _ => "one-sided" },
            Media = string.IsNullOrEmpty(o.Paper.IppMedia) ? null : o.Paper.IppMedia,
            Quality = o.Quality switch { PrintQuality.Draft => 3, PrintQuality.Best => 5, _ => 4 },
            PrintScaling = o.Scale switch { ScaleMode.FillPage => "fill", ScaleMode.ActualSize => "none", _ => "fit" },
        };
        if (opts.Sides != "one-sided" && !status.SupportsDuplex) opts.Sides = "one-sided";

        // 1. Original PDF / JPEG goes straight through
        if (src.OriginalMime == "application/pdf" && status.SupportsPdf)
        {
            opts.PageRanges = o.PageRange;
            await ipp.PrintAsync(src.OriginalBytes!, "application/pdf", src.Name, opts, ct);
            return;
        }
        var selected = PrintOptions.ParseRange(o.PageRange, src.PageCount);
        if (src.OriginalMime == "image/jpeg" && status.SupportsJpeg && src.PageCount == 1)
        {
            await ipp.PrintAsync(src.OriginalBytes!, "image/jpeg", src.Name, opts, ct);
            return;
        }

        // 2. Everything else: rebuild as PDF on the requested paper (or JPEG pages)
        if (status.SupportsPdf)
        {
            var (pw, ph) = PaperPoints(o.Paper);
            var pages = new List<PdfPageData>();
            foreach (var i in selected)
            {
                using var bmp = src.GetPage(i);
                pages.Add(new PdfPageData(ImageTools.Encode(bmp, OutputFormat.Jpeg, 92), bmp.Width, bmp.Height, src.Dpi, null, pw, ph));
            }
            opts.PrintScaling = "fit";
            await ipp.PrintAsync(PdfWriter.Build(pages, src.Name), "application/pdf", src.Name, opts, ct);
        }
        else if (status.SupportsJpeg)
        {
            foreach (var i in selected)
            {
                using var bmp = src.GetPage(i);
                await ipp.PrintAsync(ImageTools.Encode(bmp, OutputFormat.Jpeg, 92), "image/jpeg", $"{src.Name} {i + 1}", opts, ct);
            }
        }
        else
        {
            throw new InvalidOperationException(
                "This printer only accepts vendor-specific formats over IPP (no PDF or JPEG). Install its Windows driver and print through the Windows queue.");
        }
    }

    static (double W, double H) PaperPoints(PaperChoice p)
    {
        if (p.WidthIn > 0) return (p.WidthIn * 72, p.HeightIn * 72);
        return RegionInfo.CurrentRegion.IsMetric ? (8.27 * 72, 11.69 * 72) : (8.5 * 72, 11 * 72);
    }
}
