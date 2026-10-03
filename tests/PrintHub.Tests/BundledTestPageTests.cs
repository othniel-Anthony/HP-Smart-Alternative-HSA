using System.Security.Cryptography;
using PrintHub.Core.Discovery;
using PrintHub.Core.Printing;
using Xunit;

namespace PrintHub.Tests;

public class BundledTestPageTests : IClassFixture<FakePrinterFixture>
{
    // SHA-256 of print-color-test-page-basic-1.pdf as supplied
    const string ExpectedSha256 = "D4AD55D1C450F074979323A511FB3E28628D41884FED44D7E2CB6998FEE42664";
    readonly FakePrinterFixture _fake;
    public BundledTestPageTests(FakePrinterFixture fake) => _fake = fake;

    [Fact]
    public void Embedded_page_is_byte_for_byte_the_supplied_file()
    {
        var path = PrinterTools.ExtractBundledTestPage();
        Assert.Equal(ExpectedSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task Test_page_renders_in_colour_through_the_windows_pdf_engine()
    {
        var src = await PrintSource.FromFileAsync(PrinterTools.ExtractBundledTestPage());
        Assert.Equal(1, src.PageCount);
        using var bmp = src.GetPage(0);
        int colourful = 0;
        for (int y = 0; y < bmp.Height; y += 8)
            for (int x = 0; x < bmp.Width; x += 8)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 80) colourful++;
            }
        Assert.True(colourful > 200, $"expected coloured areas on the page, found {colourful} sampled pixels");
    }

    [Fact]
    public async Task Printing_it_sends_the_original_pdf_unchanged_to_the_printer()
    {
        var dev = await PrinterDiscovery.ProbeAddressAsync(_fake.Address);
        await using var session = await PrinterSession.OpenAsync(dev!);
        await PrinterTools.PrintBundledTestPageAsync(dev!, session);

        var ev = Assert.Single(_fake.Matching("IPP PRINT"));
        Assert.Contains("mime=application/pdf", ev);
        Assert.Contains("name=print-color-test-page-basic-1", ev);
        Assert.Contains("color=color", ev);
        var job = Directory.GetFiles(_fake.OutDir, "job-*.pdf").Single();
        Assert.Equal(ExpectedSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(job))));   // exactly the same bytes arrived
    }
}
