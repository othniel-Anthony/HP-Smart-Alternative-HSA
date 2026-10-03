using System.Drawing;
using System.Drawing.Drawing2D;
using PrintHub.Core.Discovery;
using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;
using PrintHub.Core.Scanning;

namespace PrintHub.Core.Printing;

public sealed class CopySettings
{
    public int Copies { get; set; } = 1;
    public bool Color { get; set; } = true;
    public ScanSource Source { get; set; } = ScanSource.Flatbed;
    public PrintQuality Quality { get; set; } = PrintQuality.Normal;
    public PaperChoice Paper { get; set; } = PaperChoice.Default;
    public DuplexMode Duplex { get; set; } = DuplexMode.Off;
    public ScaleMode Scale { get; set; } = ScaleMode.FitToPage;
    public bool Enhance { get; set; } = true;
    /// <summary>Scan both sides of a card and print them on one page.</summary>
    public bool IdCard { get; set; }
    public PrintRoute Route { get; set; } = PrintRoute.Auto;
}

/// <summary>Copy = scan on the printer, then print the result.</summary>
public static class CopyService
{
    /// <param name="promptNextSide">Called before scanning the back of an ID card; the UI asks the user to flip it.</param>
    public static async Task CopyAsync(PrinterDevice dev, PrinterSession? session, CopySettings c, Func<Task>? promptNextSide = null,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var scan = new ScanSettings
        {
            Source = c.Source,
            Color = c.Color ? ScanColor.Color : ScanColor.Grayscale,
            Dpi = c.Quality switch { PrintQuality.Draft => 150, PrintQuality.Best => 600, _ => 300 },
            AutoCrop = c.Source == ScanSource.Flatbed && !c.IdCard ? true : false,
            Paper = PaperSize.Auto,
            Enhance = c.Enhance && c.Color,
        };
        if (!c.Color && c.Enhance) scan.Color = ScanColor.Grayscale;

        var rendered = new List<Bitmap>();
        try
        {
            status?.Report(c.Source == ScanSource.Flatbed ? "Scanning…" : "Scanning from the document feeder…");
            int pageNo = 0;
            await foreach (var page in ScanService.ScanAsync(session, dev, scan, ct))
            {
                rendered.Add(ImageTools.Render(page));
                pageNo++;
                status?.Report($"Scanned page {pageNo}");
                if (c.IdCard && pageNo == 1 && promptNextSide is not null)
                {
                    await promptNextSide();
                    status?.Report("Scanning the back…");
                    await foreach (var back in ScanService.ScanAsync(session, dev, scan, ct)) { rendered.Add(ImageTools.Render(back)); break; }
                    break;
                }
            }
            if (rendered.Count == 0) throw new InvalidOperationException("Nothing was scanned.");

            List<Bitmap> toPrint = rendered;
            if (c.IdCard && rendered.Count >= 2) toPrint = new List<Bitmap> { ComposeIdCard(rendered[0], rendered[1], scan.Dpi) };

            status?.Report("Printing…");
            var src = PrintSource.FromBitmaps(toPrint, scan.Dpi, "Copy");
            var opts = new PrintOptions
            {
                Copies = c.Copies, Color = c.Color, Duplex = c.Duplex, Paper = c.Paper, Quality = c.Quality, Scale = c.IdCard ? ScaleMode.ActualSize : c.Scale,
                Route = c.Route, Collate = true,
            };
            await PrintService.PrintAsync(dev, session, src, opts, ct);
            status?.Report("Copy sent to the printer.");
        }
        finally { foreach (var b in rendered) b.Dispose(); }
    }

    /// <summary>Both sides stacked on a Letter-size page (the common "copy ID" layout).</summary>
    static Bitmap ComposeIdCard(Bitmap front, Bitmap back, int dpi)
    {
        int W = (int)(8.5 * dpi), H = (int)(11 * dpi);
        var page = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        page.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(page);
        g.Clear(System.Drawing.Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float maxW = W * 0.6f, maxH = H * 0.30f;
        void Draw(Bitmap b, float centerY)
        {
            float s = Math.Min(maxW / b.Width, maxH / b.Height);
            float w = b.Width * s, h = b.Height * s;
            g.DrawImage(b, (W - w) / 2, centerY - h / 2, w, h);
            using var pen = new Pen(System.Drawing.Color.Silver, 2);
            g.DrawRectangle(pen, (W - w) / 2, centerY - h / 2, w, h);
        }
        Draw(front, H * 0.28f);
        Draw(back, H * 0.62f);
        return page;
    }
}
