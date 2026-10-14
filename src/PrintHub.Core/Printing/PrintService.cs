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
        // does). If it refuses, nothing was printed and the driver route below still runs. Callers that ask for it (the colour test page)
        // get the same on any connection, and for printers that only take JPEG: the printer makes the copies instead of the driver.
        if (o.Route == PrintRoute.Auto && route == PrintRoute.WindowsDriver && session?.Ipp is { } directIpp
            && (o.PreferDirectIpp || (session.ViaUsb && src.OriginalMime == "application/pdf")) && string.IsNullOrEmpty(o.PrintToFilePath))
        {
            bool accepts = false;
            try
            {
                var st = await directIpp.GetStatusAsync(ct);
                accepts = st.SupportsPdf || (o.PreferDirectIpp && (st.SupportsPwgRaster || st.SupportsJpeg));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Diag.Log("Print: could not ask the printer about its formats over IPP: " + ex.Message); }
            if (accepts)
            {
                try
                {
                    await PrintViaIppAsync(directIpp, src, o, ct);
                    Diag.Log($"Print: sent straight to the printer ({(session.ViaUsb ? "over USB" : "over the network")}) in {sw.ElapsedMilliseconds} ms");
                    return;
                }
                catch (Exception ex) when (ex is IppException or InvalidOperationException) { Diag.Log($"Print: the printer refused the direct job ({ex.Message}); using the Windows driver"); }
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
            await SendWithCopiesAsync(ipp, src.OriginalBytes!, "application/pdf", src.Name, opts, ct);
            return;
        }
        var selected = PrintOptions.ParseRange(o.PageRange, src.PageCount);
        if (src.OriginalMime == "image/jpeg" && status.SupportsJpeg && src.PageCount == 1)
        {
            await SendWithCopiesAsync(ipp, src.OriginalBytes!, "image/jpeg", src.Name, opts, ct);
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
            await SendWithCopiesAsync(ipp, PdfWriter.Build(pages, src.Name), "application/pdf", src.Name, opts, ct);
        }
        else if (status.SupportsPwgRaster)
        {
            // Raster is what a driverless printer prints from directly. A JPEG is decoded and treated as a photo: measured on an HP Smart Tank 580 over USB, the
            // colour test page took 127-187 s as a JPEG and 35 s as PWG raster (237 s through the Windows driver).
            var (wPt, hPt, keyword) = SheetFor(o.Paper, status.MediaDefault);
            int dpi = status.PwgRasterResolutions.Count == 0 || status.PwgRasterResolutions.Contains(300) ? 300 : status.PwgRasterResolutions.Min();
            var bitmaps = selected.Select(i => src.GetPage(i)).ToList();
            try
            {
                opts.Media = keyword; opts.PrintScaling = null;
                bool gray = !opts.Color;
                int wanted = Math.Max(1, o.Copies);
                int max = wanted > 1 ? await ipp.GetMaxCopiesAsync("image/pwg-raster", ct) ?? int.MaxValue : int.MaxValue;
                if (max >= wanted)
                {
                    await ipp.PrintAsync(PwgRaster.EncodePages(bitmaps, wPt, hPt, dpi, keyword, 1, gray), "image/pwg-raster", src.Name, opts, ct);
                }
                else
                {
                    // The printer makes one copy of a raster job whatever "copies" says (an HP Smart Tank 580 lists 1-1 for raster, 1-99 for JPEG),
                    // so the copies are in the document itself: whole sets, in jobs of at most ten so a large number does not build a huge file.
                    Diag.Log($"Print: the printer takes at most {max} copy per raster job; putting the {wanted} copies in the document");
                    opts.Copies = 1;
                    byte[]? full = null;
                    for (int left = wanted; left > 0;)
                    {
                        int n = Math.Min(left, MaxSetsPerRasterJob);
                        byte[] doc = n == MaxSetsPerRasterJob ? (full ??= PwgRaster.EncodePages(bitmaps, wPt, hPt, dpi, keyword, 1, gray, repeat: n))
                                                              : PwgRaster.EncodePages(bitmaps, wPt, hPt, dpi, keyword, 1, gray, repeat: n);
                        await PrintWhenReadyAsync(ipp, doc, "image/pwg-raster", src.Name, opts, ct);
                        left -= n;
                    }
                }
            }
            finally { foreach (var b in bitmaps) b.Dispose(); }
        }
        else if (status.SupportsJpeg)
        {
            foreach (var i in selected)
            {
                using var bmp = src.GetPage(i);
                await SendWithCopiesAsync(ipp, EncodePageAsJpeg(bmp, src, o.Paper), "image/jpeg", $"{src.Name} {i + 1}", opts, ct);
            }
        }
        else
        {
            throw new InvalidOperationException(
                "This printer only accepts vendor-specific formats over IPP (no PDF or JPEG). Install its Windows driver and print through the Windows queue.");
        }
    }

    /// <summary>
    /// A page as a JPEG for a printer that takes only that. A page rendered from a PDF carries no resolution of its own (the renderer tags it with the screen's: 96 or 120 dpi,
    /// which makes it 26 x 37 inches), and a printer that obeys the tag instead of scaling to fit then prints a huge crop. It is tagged so that its natural size is the paper it
    /// is meant to fill (fitted inside it): the pixel size depends on the display scaling of the computer, so no fixed number is right.
    /// </summary>
    internal static byte[] EncodePageAsJpeg(System.Drawing.Bitmap bmp, PrintSource src, PaperChoice paper)
    {
        if (src.OriginalMime == "application/pdf")
        {
            var (wPt, hPt) = PaperPoints(paper);
            double dpi = Math.Max(bmp.Width / (wPt / 72.0), bmp.Height / (hPt / 72.0));
            if (dpi >= 50 && dpi <= 2400) bmp.SetResolution((float)dpi, (float)dpi);
        }
        return ImageTools.Encode(bmp, OutputFormat.Jpeg, 92);
    }

    const int MaxSetsPerRasterJob = 10;

    /// <summary>
    /// Sends one document with the requested copies. When the printer says it makes fewer copies than that for this format, the document is sent once
    /// per copy instead, so the number asked for is what comes out and not one sheet.
    /// </summary>
    static async Task SendWithCopiesAsync(IppClient ipp, byte[] data, string mime, string name, IppPrintOptions opts, CancellationToken ct)
    {
        int wanted = Math.Max(1, opts.Copies);
        int max = wanted > 1 ? await ipp.GetMaxCopiesAsync(mime, ct) ?? int.MaxValue : int.MaxValue;
        if (max >= wanted) { await ipp.PrintAsync(data, mime, name, opts, ct); return; }
        Diag.Log($"Print: the printer takes at most {max} copy per {mime} job; sending {wanted} jobs");
        opts.Copies = 1;
        for (int i = 0; i < wanted; i++) await PrintWhenReadyAsync(ipp, data, mime, name, opts, ct);
    }

    /// <summary>Print-Job, trying again a few times when the printer answers "busy" (still taking the previous job in).</summary>
    static async Task PrintWhenReadyAsync(IppClient ipp, byte[] data, string mime, string name, IppPrintOptions opts, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await ipp.PrintAsync(data, mime, name, opts, ct); return; }
            catch (IppException ex) when (ex.StatusCode == 0x0507 && attempt < 12)      // server-error-busy
            {
                Diag.Log($"Print: the printer is busy (attempt {attempt + 1}); waiting");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>The sheet a raster page is made for: the paper asked for, else the printer's own default, else A4 or Letter by region. Points and the PWG keyword.</summary>
    internal static (double WidthPt, double HeightPt, string Keyword) SheetFor(PaperChoice paper, string printerDefaultKeyword)
    {
        if (paper.WidthIn > 0 && paper.IppMedia.Length > 0) return (paper.WidthIn * 72, paper.HeightIn * 72, paper.IppMedia);
        var m = System.Text.RegularExpressions.Regex.Match(printerDefaultKeyword ?? "", @"^(?<n>[a-z0-9\-]+_[^_]+)_(?<w>\d+(?:\.\d+)?)x(?<h>\d+(?:\.\d+)?)(?<u>mm|in)$");
        if (m.Success)
        {
            double w = double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture), h = double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            double k = m.Groups["u"].Value == "mm" ? 72 / 25.4 : 72;
            if (w > 0 && h > 0) return (w * k, h * k, printerDefaultKeyword!);
        }
        var (pw, ph) = PaperPoints(PaperChoice.Default);
        bool metric = RegionInfo.CurrentRegion.IsMetric;
        return (pw, ph, metric ? "iso_a4_210x297mm" : "na_letter_8.5x11in");
    }

    static (double W, double H) PaperPoints(PaperChoice p)
    {
        if (p.WidthIn > 0) return (p.WidthIn * 72, p.HeightIn * 72);
        return RegionInfo.CurrentRegion.IsMetric ? (8.27 * 72, 11.69 * 72) : (8.5 * 72, 11 * 72);
    }
}
