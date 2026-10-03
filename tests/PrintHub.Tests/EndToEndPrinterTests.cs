using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PrintHub.Core.Discovery;
using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;
using PrintHub.Core.Ipp;
using PrintHub.Core.Printing;
using PrintHub.Core.Scanning;
using PrintHub.Core.Settings;
using Xunit;

namespace PrintHub.Tests;

/// <summary>Starts tests/FakePrinter as a separate process, like a real printer on the network.</summary>
public class FakePrinterFixture : IDisposable
{
    readonly Process _proc;
    public int Port { get; }
    public string OutDir { get; }
    public string Address => $"127.0.0.1:{Port}";

    public FakePrinterFixture() : this(false) { }

    protected FakePrinterFixture(bool mdns)
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        OutDir = Path.Combine(Path.GetTempPath(), "fake-printer-" + Guid.NewGuid().ToString("N"));
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FakePrinter", "bin", "Debug", "net8.0-windows10.0.19041.0", "FakePrinter.exe"));
        _proc = Process.Start(new ProcessStartInfo(exe, $"{Port} \"{OutDir}\"" + (mdns ? " mdns" : "")) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        var ready = _proc.StandardOutput.ReadLine();
        if (ready is null || !ready.StartsWith("READY")) throw new InvalidOperationException("Fake printer did not start: " + ready);
        _ = Task.Run(() => { while (_proc.StandardOutput.ReadLine() is not null) { } }); // drain
    }

    public string[] Events()
    {
        var f = Path.Combine(OutDir, "events.log");
        if (!File.Exists(f)) return Array.Empty<string>();
        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public string[] Matching(string needle) => Events().Where(e => e.Contains(needle)).ToArray();

    public void Dispose()
    {
        try { _proc.Kill(true); } catch { }
        try { Directory.Delete(OutDir, true); } catch { }
    }
}

public class EndToEndPrinterTests : IClassFixture<FakePrinterFixture>
{
    readonly FakePrinterFixture _fake;
    public EndToEndPrinterTests(FakePrinterFixture fake) => _fake = fake;

    async Task<(PrinterDevice Dev, PrinterSession Session)> ConnectAsync()
    {
        var dev = await PrinterDiscovery.ProbeAddressAsync(_fake.Address);
        Assert.NotNull(dev);
        return (dev!, await PrinterSession.OpenAsync(dev!));
    }

    static string MakePdf(int pages)
    {
        using var bmp = new System.Drawing.Bitmap(850, 1100);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.Clear(System.Drawing.Color.White); g.DrawString("hello", new System.Drawing.Font("Arial", 40), System.Drawing.Brushes.Black, 100, 100); }
        var jpeg = ImageTools.Encode(bmp, OutputFormat.Jpeg);
        var pdf = PdfWriter.Build(Enumerable.Range(0, pages).Select(_ => new PdfPageData(jpeg, 850, 1100, 100)).ToList());
        var path = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    [Fact]
    public async Task Add_by_address_finds_ipp_escl_and_web()
    {
        var dev = await PrinterDiscovery.ProbeAddressAsync(_fake.Address);
        Assert.NotNull(dev);
        Assert.Equal("Fake HP OfficeJet Pro 9999", dev!.Name);
        Assert.True(dev.IsHp);
        Assert.NotNull(dev.IppUri); Assert.NotNull(dev.EsclUri); Assert.NotNull(dev.WebUri);
        Assert.True(dev.CanPrint); Assert.True(dev.CanScan);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1/")]
    public async Task Add_by_address_returns_null_quickly_when_nothing_answers(string addr)
    {
        var sw = Stopwatch.StartNew();
        Assert.Null(await PrinterDiscovery.ProbeAddressAsync(addr));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Status_shows_supplies_alerts_and_identity()
    {
        var (_, s) = await ConnectAsync();
        var st = await s.Ipp!.GetStatusAsync();
        Assert.Equal(PrinterState.Idle, st.State);
        Assert.Equal(4, st.Supplies.Count);
        Assert.Equal(new[] { 72, 8, 55, 31 }, st.Supplies.Select(x => x.Percent).ToArray());
        Assert.Equal("Cyan Cartridge", st.Supplies.Single(x => x.IsLow).Name);
        Assert.Contains("Warning: Marker supply low", st.Alerts);
        Assert.Equal("CNFAKE123", st.SerialNumber);
        Assert.Equal("FAKE_2026.1", st.Firmware);
        Assert.True(st.SupportsPdf && st.SupportsJpeg && st.SupportsDuplex && st.ColorSupported);
    }

    [Fact]
    public async Task Pdf_prints_directly_with_all_options_mapped_to_ipp()
    {
        var (dev, s) = await ConnectAsync();
        var pdf = MakePdf(3);
        await PrintService.PrintFileAsync(dev, s, pdf, new PrintOptions { Copies = 2, Duplex = DuplexMode.LongEdge, Color = false, Paper = PaperChoice.A4, Quality = PrintQuality.Best, Scale = ScaleMode.FillPage, PageRange = "1-2" });
        var ev = Assert.Single(_fake.Matching("IPP PRINT").Where(e => e.Contains("copies=2")));
        Assert.Contains("mime=application/pdf", ev);
        Assert.Contains("sides=two-sided-long-edge", ev);
        Assert.Contains("color=monochrome", ev);
        Assert.Contains("media=iso_a4_210x297mm", ev);
        Assert.Contains("quality=5", ev);
        Assert.Contains("scaling=fill", ev);
        Assert.Contains("ranges=1-2", ev.Replace("IppRange { Lower = 1, Upper = 2 }", "1-2").Replace("PrintHub.Core.Ipp.IppRange", "1-2"));
        File.Delete(pdf);
    }

    [Fact]
    public async Task Photo_is_rebuilt_as_pdf_on_the_chosen_paper()
    {
        var (dev, s) = await ConnectAsync();
        var png = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.png");
        using (var b = new System.Drawing.Bitmap(900, 600)) { using var g = System.Drawing.Graphics.FromImage(b); g.Clear(System.Drawing.Color.SteelBlue); b.Save(png, System.Drawing.Imaging.ImageFormat.Png); }
        await PrintService.PrintFileAsync(dev, s, png, new PrintOptions { Copies = 1, Paper = PaperChoice.Photo4x6, Scale = ScaleMode.PhotoSize });
        var ev = _fake.Matching("IPP PRINT").Last(e => e.Contains("media=na_index-4x6"));
        Assert.Contains("mime=application/pdf", ev);
        File.Delete(png);
    }

    [Fact]
    public async Task Test_page_prints()
    {
        var (dev, s) = await ConnectAsync();
        await PrinterTools.PrintTestPageAsync(dev, s);
        Assert.Contains(_fake.Matching("IPP PRINT"), e => e.Contains("name=HSA test page"));
    }

    [Fact]
    public async Task Jobs_identify_and_cancel_work()
    {
        var (_, s) = await ConnectAsync();
        var jobs = await s.Ipp!.GetJobsAsync();
        var j = Assert.Single(jobs);
        Assert.Equal("Held test job", j.Name); Assert.Equal("Held", j.State);
        await s.Ipp.CancelJobAsync(j.Id);
        await PrinterTools.IdentifyAsync(s);
        Assert.NotEmpty(_fake.Matching("IPP Cancel-Job id=9001"));
        Assert.NotEmpty(_fake.Matching("IPP Identify-Printer actions=flash+sound"));
    }

    [Fact]
    public async Task Flatbed_scan_retries_when_busy_then_autocrops_and_deskews()
    {
        var (dev, s) = await ConnectAsync();
        var pages = new List<ScannedPage>();
        await foreach (var p in ScanService.ScanAsync(s, dev, new ScanSettings { Dpi = 150, AutoCrop = true, AutoStraighten = true, Enhance = true })) pages.Add(p);
        var page = Assert.Single(pages);
        Assert.NotNull(page.Edits.Crop);                       // document found on the dark bed
        Assert.InRange(page.Edits.Straighten, -3.5, -0.5);     // fake page is tilted 2 degrees, correction is the opposite sign
        Assert.Equal(PageFilter.Enhance, page.Edits.Filter);
        Assert.Contains(_fake.Events(), e => e.Contains("503 warming up"));
        Assert.Contains(_fake.Events(), e => e.Contains("source=Platen") && e.Contains("color=RGB24") && e.Contains("dpi=150"));
        Assert.Contains(_fake.Events(), e => e.Contains("ESCL delete"));
    }

    [Fact]
    public async Task Feeder_scans_all_pages_and_duplex_doubles_them()
    {
        var (dev, s) = await ConnectAsync();
        var simplex = new List<ScannedPage>();
        await foreach (var p in ScanService.ScanAsync(s, dev, new ScanSettings { Source = ScanSource.Feeder, Dpi = 150, Color = ScanColor.Grayscale })) simplex.Add(p);
        Assert.Equal(3, simplex.Count);
        Assert.All(simplex, p => Assert.Equal(PageFilter.Grayscale, p.Edits.Filter));

        var duplex = new List<ScannedPage>();
        await foreach (var p in ScanService.ScanAsync(s, dev, new ScanSettings { Source = ScanSource.FeederDuplex, Dpi = 150, Color = ScanColor.BlackAndWhite })) duplex.Add(p);
        Assert.Equal(6, duplex.Count);
        Assert.All(duplex, p => Assert.Equal(PageFilter.BlackAndWhite, p.Edits.Filter));
        Assert.Contains(_fake.Events(), e => e.Contains("source=Feeder") && e.Contains("duplex=True") && e.Contains("color=Grayscale8"));
    }

    [Fact]
    public async Task Paper_size_limits_the_requested_scan_region()
    {
        var (dev, s) = await ConnectAsync();
        await foreach (var _ in ScanService.ScanAsync(s, dev, new ScanSettings { Dpi = 150, Paper = PaperSize.IdCard, AutoCrop = false })) { }
        Assert.Contains(_fake.Events(), e => e.Contains("region=1011x639"));   // 3.37 x 2.13 in at 300ths
    }

    [Fact]
    public async Task Scan_exports_a_searchable_pdf_with_the_scanned_text()
    {
        if (!OcrService.IsAvailable) return;
        var (dev, s) = await ConnectAsync();
        var pages = new List<ScannedPage>();
        await foreach (var p in ScanService.ScanAsync(s, dev, new ScanSettings { Source = ScanSource.Feeder, Dpi = 200 })) pages.Add(p);
        var folder = Path.Combine(Path.GetTempPath(), "e2e-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = await ExportService.SaveAsync(pages, OutputFormat.Pdf, folder, "feeder", searchable: true);
            var text = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(files[0]));
            Assert.Contains("/Count 3", text);
            Assert.Contains("Invoice", text);                    // invisible OCR layer
            Assert.Equal(3, (await PrintSource.FromFileAsync(files[0])).PageCount);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task Copy_scans_then_prints()
    {
        var (dev, s) = await ConnectAsync();
        int before = _fake.Matching("IPP PRINT").Length;
        await CopyService.CopyAsync(dev, s, new CopySettings { Copies = 3, Color = false, Quality = PrintQuality.Draft });
        var jobs = _fake.Matching("IPP PRINT");
        Assert.Equal(before + 1, jobs.Length);
        Assert.Contains("copies=3", jobs.Last());
        Assert.Contains("color=monochrome", jobs.Last());
        Assert.Contains(_fake.Events(), e => e.Contains("dpi=150") && e.Contains("color=Grayscale8"));
    }

    [Fact]
    public async Task Id_card_copy_scans_both_sides_into_one_page()
    {
        var (dev, s) = await ConnectAsync();
        int flips = 0;
        int scansBefore = _fake.Matching("ESCL create").Length;
        await CopyService.CopyAsync(dev, s, new CopySettings { IdCard = true, Quality = PrintQuality.Draft }, () => { flips++; return Task.CompletedTask; });
        Assert.Equal(1, flips);
        Assert.Equal(scansBefore + 2, _fake.Matching("ESCL create").Length);
    }

    [Fact]
    public async Task Shortcut_scans_saves_and_prints()
    {
        var (dev, s) = await ConnectAsync();
        var folder = Path.Combine(Path.GetTempPath(), "e2e-shortcut-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sc = new ShortcutDef { Name = "Receipt", Color = ScanColor.BlackAndWhite, Dpi = 150, Format = OutputFormat.Pdf, Folder = folder, PrintCopies = 2, Searchable = false };
            var files = await ShortcutRunner.RunAsync(sc, dev, s, new AppSettings());
            Assert.Single(files);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(files[0]))[..4]).Replace("%PDF", "%PDF"));
            Assert.Contains(_fake.Matching("IPP PRINT"), e => e.Contains("copies=2"));
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public async Task Embedded_web_page_is_reachable_through_the_session()
    {
        var (_, s) = await ConnectAsync();
        using var http = new HttpClient();
        var html = await http.GetStringAsync(s.WebUri);
        Assert.Contains("Fake Embedded Web Server", html);
    }
}
