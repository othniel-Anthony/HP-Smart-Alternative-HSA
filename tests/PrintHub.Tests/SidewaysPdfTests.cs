using System.Drawing;
using PrintHub.Core.Imaging;
using PrintHub.Core.Printing;
using PrintHub.Core.Scanning;
using Xunit;

namespace PrintHub.Tests;

public class SidewaysPdfTests
{
    static byte[] SidewaysScan()
    {
        using var bmp = new Bitmap(1700, 2200);
        bmp.SetResolution(200, 200);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var f = new Font("Arial", 52, GraphicsUnit.Pixel);
            string[] lines = { "Quarterly report for the finance team", "Invoice number twenty total amount due", "Please remember to approve every payment", "Thank you for your continued support", "Contact the office with any questions", "Meeting notes follow on the next page", "Remaining balance will carry forward", "Regards from the accounting department" };
            for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], f, Brushes.Black, 120, 160 + i * 110);
        }
        bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);   // scanned lying on its side: 2200 x 1700
        return ImageTools.Encode(bmp, OutputFormat.Png);
    }

    [Fact]
    public async Task Searchable_pdf_turns_a_sideways_page_upright_and_keeps_the_text_layer()
    {
        if (!OcrService.IsAvailable) return;
        var folder = Path.Combine(Path.GetTempPath(), "hsa-sideways-" + Guid.NewGuid().ToString("N"));
        try
        {
            var page = new ScannedPage(SidewaysScan(), 200);
            var files = await ExportService.SaveAsync(new[] { page }, OutputFormat.Pdf, folder, "sideways", searchable: true);

            var text = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(files[0]));
            Assert.Contains("Invoice", text);                           // text layer exists
            var (w, h) = (await PrintSource.FromFileAsync(files[0])).GetSize(0);
            Assert.True(h > w, $"page should be portrait after turning, got {w}x{h}");
            Assert.Equal(0, page.Edits.ExtraTurn);                      // the user's own edits are untouched
            Assert.True(page.Edits.IsIdentity);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task Non_searchable_export_leaves_the_page_exactly_as_scanned()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hsa-sideways2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = await ExportService.SaveAsync(new[] { new ScannedPage(SidewaysScan(), 200) }, OutputFormat.Pdf, folder, "asis", searchable: false);
            var (w, h) = (await PrintSource.FromFileAsync(files[0])).GetSize(0);
            Assert.True(w > h, $"page should stay landscape, got {w}x{h}");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
