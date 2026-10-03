using PrintHub.Core.Imaging;
using PrintHub.Core.Printing;
using Xunit;

namespace PrintHub.Tests;

/// <summary>Real GDI printing through the Windows "Microsoft Print to PDF" driver (no paper, no dialog).</summary>
public class WindowsDriverPrintTests
{
    const string Driver = "Microsoft Print to PDF";
    static bool Available => SpoolerPrinters.List().Any(p => p.Name == Driver);

    static PrintSource Source(int pages, int w = 1275, int h = 1650)
    {
        var bmps = Enumerable.Range(0, pages).Select(i =>
        {
            var b = new System.Drawing.Bitmap(w, h);
            using var g = System.Drawing.Graphics.FromImage(b);
            g.Clear(System.Drawing.Color.White);
            g.FillEllipse(System.Drawing.Brushes.Red, 200, 200, 600, 600);
            g.DrawString($"Page {i + 1}", new System.Drawing.Font("Arial", 80), System.Drawing.Brushes.Black, 200, 900);
            return b;
        }).ToList();
        try { return PrintSource.FromBitmaps(bmps, 150, "driver-test"); } finally { bmps.ForEach(b => b.Dispose()); }
    }

    static async Task<string> PrintToPdf(PrintSource src, PrintOptions o)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hsa-driver-{Guid.NewGuid():N}.pdf");
        o.PrintToFilePath = path;
        var task = WindowsPrintService.PrintAsync(Driver, src, o);
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))) != task) throw new TimeoutException("Printing did not finish (a dialog may be waiting).");
        await task;
        for (int i = 0; i < 40 && !(File.Exists(path) && new FileInfo(path).Length > 0); i++) await Task.Delay(250);
        return path;
    }

    [Fact]
    public async Task Multi_page_document_prints_through_the_windows_driver()
    {
        if (!Available) return;
        var path = await PrintToPdf(Source(3), new PrintOptions());
        try
        {
            Assert.True(File.Exists(path));
            var back = await PrintSource.FromFileAsync(path);
            Assert.Equal(3, back.PageCount);
            using var p = back.GetPage(0);
            // red disc printed near the top-left of the page, so the drawing really reached the driver
            bool red = false;
            for (int y = 0; y < p.Height / 3 && !red; y += 20) for (int x = 0; x < p.Width / 2 && !red; x += 20) { var c = p.GetPixel(x, y); red = c.R > 200 && c.G < 80 && c.B < 80; }
            Assert.True(red, "no red pixels found in the printed page");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Page_range_selects_pages()
    {
        if (!Available) return;
        var path = await PrintToPdf(Source(4), new PrintOptions { PageRange = "2-3" });
        try { Assert.Equal(2, (await PrintSource.FromFileAsync(path)).PageCount); }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Landscape_pages_rotate_the_paper()
    {
        if (!Available) return;
        var path = await PrintToPdf(Source(1, 1650, 1275), new PrintOptions());
        try
        {
            var (w, h) = (await PrintSource.FromFileAsync(path)).GetSize(0);
            Assert.True(w > h, $"expected a landscape page, got {w}x{h}");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Pdf_file_round_trips_through_print_pipeline()
    {
        if (!Available) return;
        var pdf = Path.Combine(Path.GetTempPath(), $"hsa-in-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(pdf, ExportService_Pdf());
        var outPath = Path.Combine(Path.GetTempPath(), $"hsa-out-{Guid.NewGuid():N}.pdf");
        try
        {
            var src = await PrintSource.FromFileAsync(pdf);
            var task = WindowsPrintService.PrintAsync(Driver, src, new PrintOptions { PrintToFilePath = outPath, Scale = ScaleMode.FillPage });
            await task.WaitAsync(TimeSpan.FromSeconds(60));
            for (int i = 0; i < 40 && !(File.Exists(outPath) && new FileInfo(outPath).Length > 0); i++) await Task.Delay(250);
            Assert.Equal(2, (await PrintSource.FromFileAsync(outPath)).PageCount);
        }
        finally { foreach (var f in new[] { pdf, outPath }) try { File.Delete(f); } catch { } }
    }


    [Fact]
    public async Task Bundled_colour_test_page_prints_in_colour_through_the_windows_driver()
    {
        if (!Available) return;
        var src = await PrintSource.FromFileAsync(PrinterTools.ExtractBundledTestPage());
        var path = await PrintToPdf(src, new PrintOptions { Color = true, Scale = ScaleMode.FitToPage });
        try
        {
            var back = await PrintSource.FromFileAsync(path);
            Assert.Equal(1, back.PageCount);
            using var p = back.GetPage(0);
            int colourful = 0;
            for (int y = 0; y < p.Height; y += 10) for (int x = 0; x < p.Width; x += 10) { var c = p.GetPixel(x, y); if (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 80) colourful++; }
            Assert.True(colourful > 150, $"printed page has no colour ({colourful})");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    static byte[] ExportService_Pdf()
    {
        using var b = new System.Drawing.Bitmap(850, 1100);
        using (var g = System.Drawing.Graphics.FromImage(b)) g.Clear(System.Drawing.Color.LightYellow);
        var jpeg = ImageTools.Encode(b, OutputFormat.Jpeg);
        return PdfWriter.Build(new[] { new PdfPageData(jpeg, 850, 1100, 100), new PdfPageData(jpeg, 850, 1100, 100) });
    }
}
