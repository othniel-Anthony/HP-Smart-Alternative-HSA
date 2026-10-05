using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;

namespace PrintHub.Core.Printing;

/// <summary>Prints through a Windows print queue (the vendor driver), so every driver feature keeps working.</summary>
public static class WindowsPrintService
{
    public static Task PrintAsync(string printerName, PrintSource src, PrintOptions o, string? docName = null, CancellationToken ct = default) =>
        Task.Run(() => Print(printerName, src, o, docName ?? src.Name, ct), ct);

    static void Print(string printerName, PrintSource src, PrintOptions o, string docName, CancellationToken ct)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var pages = PrintOptions.ParseRange(o.PageRange, src.PageCount);
        int cursor = 0;
        Bitmap? current = null;
        int currentIndex = -1;

        Bitmap Get(int idx)
        {
            if (currentIndex != idx) { current?.Dispose(); current = src.GetPage(pages[idx]); currentIndex = idx; }
            return current!;
        }

        using var doc = new PrintDocument { DocumentName = docName };
        var ps = doc.PrinterSettings;
        ps.PrinterName = printerName;
        if (!ps.IsValid) throw new InvalidOperationException($"Windows does not know a printer named '{printerName}'.");

        if (!string.IsNullOrEmpty(o.PrintToFilePath)) { ps.PrintToFile = true; ps.PrintFileName = o.PrintToFilePath; }
        ps.Copies = (short)Math.Clamp(o.Copies, 1, 999);
        ps.Collate = o.Collate;

        // Everything below is a preference. A driver that rejects one of them must not stop the page from printing.
        void Try(string what, Action a) { try { a(); } catch (Exception ex) { Diag.Log($"Windows print: could not apply {what}: {ex.Message}"); } }
        Try("duplex", () => { if (o.Duplex != DuplexMode.Off || ps.CanDuplex) ps.Duplex = o.Duplex switch { DuplexMode.LongEdge => Duplex.Vertical, DuplexMode.ShortEdge => Duplex.Horizontal, _ => Duplex.Simplex }; });
        Try("colour", () => doc.DefaultPageSettings.Color = o.Color && ps.SupportsColor);
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        if (o.Paper != PaperChoice.Default)
            Try("paper size", () =>
            {
                var match = ps.PaperSizes.Cast<PaperSize>().FirstOrDefault(p =>
                        o.Paper.WindowsNames.Any(n => p.PaperName.Equals(n, StringComparison.OrdinalIgnoreCase) || p.PaperName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    ?? ps.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => Math.Abs(p.Width - o.Paper.WidthIn * 100) < 8 && Math.Abs(p.Height - o.Paper.HeightIn * 100) < 8);
                if (match is not null) doc.DefaultPageSettings.PaperSize = match;
            });

        Try("print quality", () =>
        {
            var wantRes = o.Quality switch { PrintQuality.Draft => PrinterResolutionKind.Draft, PrintQuality.Best => PrinterResolutionKind.High, _ => PrinterResolutionKind.Medium };
            var res = ps.PrinterResolutions.Cast<PrinterResolution>().FirstOrDefault(r => r.Kind == wantRes);
            if (res is not null) doc.DefaultPageSettings.PrinterResolution = res;
        });
        Diag.Log($"Windows print '{printerName}': settings ready after {total.ElapsedMilliseconds} ms ({pages.Count} page(s), {o.Copies} copies)");

        doc.QueryPageSettings += (_, e) =>
        {
            ct.ThrowIfCancellationRequested();
            if (cursor >= pages.Count) return;
            var (w, h) = src.GetSize(pages[cursor]);
            e.PageSettings.Landscape = w > h && o.Scale != ScaleMode.PhotoSize; // rotate paper to match the page
        };

        doc.PrintPage += (_, e) =>
        {
            ct.ThrowIfCancellationRequested();
            var bmp = Get(cursor);
            var g = e.Graphics!;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float margin = o.Borderless ? 0 : 20; // 1/100 inch units
            var area = new RectangleF(e.PageBounds.X + margin, e.PageBounds.Y + margin, e.PageBounds.Width - 2 * margin, e.PageBounds.Height - 2 * margin);
            var dest = Place(bmp.Width, bmp.Height, src.Dpi, area, o);
            if (o.Scale == ScaleMode.FillPage) g.SetClip(area);
            g.DrawImage(bmp, dest);

            cursor++;
            e.HasMorePages = cursor < pages.Count;
        };

        try { doc.Print(); Diag.Log($"Windows print '{printerName}': handed to the spooler after {total.ElapsedMilliseconds} ms"); }
        catch (InvalidPrinterException ex) { throw new InvalidOperationException($"The printer '{printerName}' is not available: {ex.Message}", ex); }
        finally { current?.Dispose(); }
    }

    static RectangleF Place(int w, int h, int dpi, RectangleF area, PrintOptions o)
    {
        switch (o.Scale)
        {
            case ScaleMode.ActualSize:
            {
                float pw = w * 100f / dpi, ph = h * 100f / dpi;
                return new RectangleF(area.X + (area.Width - pw) / 2, area.Y + (area.Height - ph) / 2, pw, ph);
            }
            case ScaleMode.PhotoSize:
            {
                float pw = (float)(o.PhotoWidthIn * 100), ph = (float)(o.PhotoHeightIn * 100);
                if ((w > h) != (pw > ph)) (pw, ph) = (ph, pw); // match the photo's orientation
                float s = Math.Min(pw / w, ph / h);
                return new RectangleF(area.X, area.Y, w * s, h * s);
            }
            case ScaleMode.FillPage:
            {
                float s = Math.Max(area.Width / w, area.Height / h);
                float dw = w * s, dh = h * s;
                return new RectangleF(area.X + (area.Width - dw) / 2, area.Y + (area.Height - dh) / 2, dw, dh);
            }
            default:
            {
                float s = Math.Min(area.Width / w, area.Height / h);
                float dw = w * s, dh = h * s;
                return new RectangleF(area.X + (area.Width - dw) / 2, area.Y + (area.Height - dh) / 2, dw, dh);
            }
        }
    }

    /// <summary>Paper sizes the Windows driver offers, for the UI.</summary>
    public static List<string> GetPaperSizes(string printerName)
    {
        try
        {
            var ps = new PrinterSettings { PrinterName = printerName };
            return ps.IsValid ? ps.PaperSizes.Cast<PaperSize>().Select(p => p.PaperName).ToList() : new();
        }
        catch { return new(); }
    }

    public static (bool Color, bool Duplex) GetCapabilities(string printerName)
    {
        try
        {
            var ps = new PrinterSettings { PrinterName = printerName };
            return ps.IsValid ? (ps.SupportsColor, ps.CanDuplex) : (true, false);
        }
        catch { return (true, false); }
    }
}
