using System.Drawing;
using PrintHub.Core.Imaging;
using Xunit;

namespace PrintHub.Tests;

public class OcrOrientationTests
{
    static byte[] Page(RotateFlipType turn)
    {
        using var bmp = new System.Drawing.Bitmap(1700, 2200);
        bmp.SetResolution(200, 200);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var f = new System.Drawing.Font("Arial", 52, System.Drawing.GraphicsUnit.Pixel);
            string[] lines = { "Quarterly report for the finance team", "Invoice number twenty total amount due", "Please remember to approve every payment", "Thank you for your continued support", "Contact the office with any questions", "Meeting notes follow on the next page", "Remaining balance will carry forward", "Regards from the accounting department" };
            for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], f, System.Drawing.Brushes.Black, 120, 160 + i * 110);
        }
        bmp.RotateFlip(turn);
        return ImageTools.Encode(bmp, OutputFormat.Png);
    }

    [Fact]
    public async Task Upright_page_is_read_without_extra_passes()
    {
        if (!OcrService.IsAvailable) return;
        var (r, rot) = await OcrService.RecognizeUprightAsync(Page(RotateFlipType.RotateNoneFlipNone));
        Assert.Equal(0, rot);
        Assert.Contains("Invoice", r.Text);
    }

    [Theory]
    [InlineData(RotateFlipType.Rotate90FlipNone, 270)]    // page turned clockwise by 90 needs 270 more to be upright
    [InlineData(RotateFlipType.Rotate270FlipNone, 90)]
    [InlineData(RotateFlipType.Rotate180FlipNone, 180)]
    public async Task Sideways_and_upside_down_pages_are_turned_upright_before_reading(RotateFlipType turn, int expectedCorrection)
    {
        if (!OcrService.IsAvailable) return;
        var (r, rot) = await OcrService.RecognizeUprightAsync(Page(turn));
        // Windows OCR can already read upside-down text on its own, so for 180 either route is acceptable
        if (expectedCorrection == 180) Assert.True(rot is 0 or 180); else Assert.Equal(expectedCorrection, rot);
        Assert.Contains("Invoice", r.Text);
        Assert.Contains("accounting", r.Text);
    }
}
