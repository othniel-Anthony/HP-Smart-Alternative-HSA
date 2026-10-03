using System.Text;
using PrintHub.Core.Discovery;
using PrintHub.Core.Escl;
using PrintHub.Core.Http;
using PrintHub.Core.Ipp;
using Xunit;

namespace PrintHub.Tests;

public class IppTests
{
    [Fact]
    public void Request_roundtrips()
    {
        var req = IppMessage.CreateRequest(IppOp.PrintJob, new Uri("http://10.0.0.5:631/ipp/print"));
        req.Add(IppTag.JobAttributes, IppTag.Integer, "copies", 3);
        req.Add(IppTag.JobAttributes, IppTag.Keyword, "sides", "two-sided-long-edge");
        req.Add(IppTag.JobAttributes, IppTag.RangeOfInteger, "page-ranges", new IppRange(1, 3), new IppRange(7, 7));
        var back = IppMessage.Decode(req.Encode(), out var off);
        Assert.Equal(IppOp.PrintJob, back.Code);
        Assert.Equal("ipp://10.0.0.5:631/ipp/print", back.GetString("printer-uri"));
        Assert.Equal(3, back.GetInt("copies"));
        Assert.Equal(2, back.Get("page-ranges")!.Values.Count);
        Assert.Equal(new IppRange(7, 7), back.Get("page-ranges")!.Values[1]);
        Assert.Equal(req.Encode().Length, off);
    }

    [Fact]
    public void Decodes_response_with_collection_and_multivalues()
    {
        var ms = new MemoryStream();
        void B(params byte[] b) => ms.Write(b);
        void S(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void Attr(byte tag, string name, byte[] val) { ms.WriteByte(tag); S((ushort)name.Length); ms.Write(Encoding.ASCII.GetBytes(name)); S((ushort)val.Length); ms.Write(val); }
        B(2, 0, 0, 0, 0, 0, 0, 1);              // IPP 2.0, successful-ok, request-id 1
        B(0x04);                                // printer attributes
        Attr(IppTag.Name, "printer-info", Encoding.UTF8.GetBytes("HP Test"));
        // collection with nested member
        ms.WriteByte(IppTag.BegCollection); S(9); ms.Write(Encoding.ASCII.GetBytes("media-col")); S(0);
        Attr(IppTag.MemberAttrName, "", Encoding.ASCII.GetBytes("media-size"));
        ms.WriteByte(IppTag.BegCollection); S(0); S(0);
        Attr(IppTag.MemberAttrName, "", Encoding.ASCII.GetBytes("x-dimension")); Attr(IppTag.Integer, "", new byte[] { 0, 0, 0, 5 });
        ms.WriteByte(IppTag.EndCollection); S(0); S(0);
        ms.WriteByte(IppTag.EndCollection); S(0); S(0);
        // marker levels: 2 values (second has empty name)
        Attr(IppTag.Integer, "marker-levels", new byte[] { 0, 0, 0, 80 });
        ms.WriteByte(IppTag.Integer); S(0); S(4); B(0, 0, 0, 8);
        Attr(IppTag.Keyword, "marker-names", Encoding.ASCII.GetBytes("Black"));
        ms.WriteByte(IppTag.Keyword); S(0); S(5); ms.Write(Encoding.ASCII.GetBytes("Cyan"));
        B(0x03);

        var m = IppMessage.Decode(ms.ToArray(), out _);
        Assert.True(m.Successful);
        Assert.Equal("HP Test", m.GetString("printer-info"));
        var st = PrinterStatus.FromMessage(m);
        Assert.Equal(2, st.Supplies.Count);
        Assert.Equal("Black", st.Supplies[0].Name);
        Assert.Equal(80, st.Supplies[0].Percent);
        Assert.Equal(8, st.Supplies[1].Percent);
        Assert.True(st.Supplies[1].IsLow);
    }

    [Fact]
    public void Humanizes_state_reasons()
    {
        Assert.Equal("Error: Media jam", PrinterStatus.Humanize("media-jam-error"));
        Assert.Equal("Warning: Marker supply low", PrinterStatus.Humanize("marker-supply-low-warning"));
    }
}

public class HttpTests
{
    static async Task<HttpMessage?> Parse(string raw, bool response, bool head = false) =>
        await new HttpReader(new MemoryStream(Encoding.ASCII.GetBytes(raw))).ReadAsync(response, head);

    [Fact]
    public async Task Reads_content_length_response()
    {
        var m = (await Parse("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nX: y\r\n\r\nhello", true))!;
        Assert.Equal(200, m.StatusCode);
        Assert.Equal("hello", Encoding.ASCII.GetString(m.Body));
    }

    [Fact]
    public async Task Reads_chunked_response_and_reserialises_with_length()
    {
        var m = (await Parse("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6;ext=1\r\n world\r\n0\r\nTrailer: x\r\n\r\n", true))!;
        Assert.Equal("hello world", Encoding.ASCII.GetString(m.Body));
        var wire = Encoding.ASCII.GetString(m.Serialize());
        Assert.Contains("Content-Length: 11", wire);
        Assert.DoesNotContain("chunked", wire);
    }

    [Fact]
    public async Task Head_and_204_have_no_body()
    {
        Assert.Empty((await Parse("HTTP/1.1 200 OK\r\nContent-Length: 50\r\n\r\n", true, head: true))!.Body);
        Assert.Empty((await Parse("HTTP/1.1 204 No Content\r\n\r\n", true))!.Body);
    }

    [Fact]
    public async Task Two_pipelined_messages_share_the_buffer()
    {
        var r = new HttpReader(new MemoryStream(Encoding.ASCII.GetBytes("GET /a HTTP/1.1\r\nHost: x\r\n\r\nPOST /b HTTP/1.1\r\nContent-Length: 2\r\n\r\nhi")));
        Assert.Equal("GET /a HTTP/1.1", (await r.ReadAsync(false))!.StartLine);
        var second = (await r.ReadAsync(false))!;
        Assert.Equal("POST", second.Method);
        Assert.Equal("hi", Encoding.ASCII.GetString(second.Body));
        Assert.Null(await r.ReadAsync(false));
    }
}

public class EsclTests
{
    [Fact]
    public void Builds_valid_settings()
    {
        var xml = EsclClient.BuildSettings(new EsclScanRequest { Source = ScanSource.FeederDuplex, Color = ScanColor.Grayscale, Dpi = 200 },
            new EsclSourceCaps { MaxWidth = 2550, MaxHeight = 4200 });
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Contains("Feeder", xml);
        Assert.Contains("Grayscale8", xml);
        Assert.Contains("<scan:Duplex>true</scan:Duplex>", xml);
        Assert.Contains("<pwg:Height>4200</pwg:Height>", xml);
        Assert.NotNull(doc.Root);
    }
}

public class MdnsTests
{
    static byte[] Name(string n) { var ms = new MemoryStream(); foreach (var l in n.Split('.')) { ms.WriteByte((byte)l.Length); ms.Write(Encoding.ASCII.GetBytes(l)); } ms.WriteByte(0); return ms.ToArray(); }

    [Fact]
    public void Parses_ptr_srv_txt_a_with_compression()
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void Rec(byte[] name, int type, byte[] rdata) { ms.Write(name); U16(type); U16(1); U16(0); U16(120); U16(rdata.Length); ms.Write(rdata); }
        U16(0); U16(0x8400); U16(0); U16(4); U16(0); U16(0);

        var inst = Name("HP Printer._ipp._tcp.local");
        Rec(Name("_ipp._tcp.local"), 12, inst);
        var srv = new List<byte> { 0, 0, 0, 0, 0x02, 0x77 }; srv.AddRange(Name("printer.local"));
        Rec(inst, 33, srv.ToArray());
        var txt = new List<byte>(); foreach (var kv in new[] { "rp=ipp/print", "ty=HP Test 100" }) { txt.Add((byte)kv.Length); txt.AddRange(Encoding.ASCII.GetBytes(kv)); }
        Rec(inst, 16, txt.ToArray());
        Rec(Name("printer.local"), 1, new byte[] { 192, 168, 1, 50 });

        var db = new MdnsClient.Db();
        db.Parse(ms.ToArray(), (int)ms.Length);
        var svc = Assert.Single(db.Build());
        Assert.Equal("HP Printer", svc.Instance);
        Assert.Equal("_ipp._tcp", svc.Type);
        Assert.Equal(631, svc.Port);
        Assert.Equal("ipp/print", svc.Txt["rp"]);
        Assert.Equal("192.168.1.50", svc.Addresses[0].ToString());
    }
}

public class MatchingTests
{
    [Theory]
    [InlineData("HP Smart Tank 580-590 series (NET)", "HP Smart Tank 580 series", true)]
    [InlineData("HP OfficeJet Pro 9730 Series", "HP OfficeJet Pro 9730 series (USB)", true)]
    [InlineData("EPSON L3250 Series", "EPSON ET-16650 Series", false)]
    [InlineData("HP Smart Tank 580", "HP OfficeJet Pro 9730", false)]
    public void Fuzzy_match(string a, string b, bool expected) => Assert.Equal(expected, PrinterDevice.Similar(a, b));
}

public class ImagingTests
{
    // A "scan": dark bed with a white page containing text-like lines, optionally skewed.
    static byte[] FakeScan(double skewDeg = 0)
    {
        using var bmp = new System.Drawing.Bitmap(1200, 1600);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(40, 40, 40));
            g.TranslateTransform(600, 800); g.RotateTransform((float)skewDeg); g.TranslateTransform(-600, -800);
            g.FillRectangle(System.Drawing.Brushes.White, 150, 200, 900, 1200);
            for (int y = 260; y < 1340; y += 40) g.FillRectangle(System.Drawing.Brushes.Black, 200, y, 800, 12);
        }
        return PrintHub.Core.Imaging.ImageTools.Encode(bmp, PrintHub.Core.Imaging.OutputFormat.Png);
    }

    [Fact]
    public void AutoCrop_finds_the_page_on_the_bed()
    {
        using var bmp = PrintHub.Core.Imaging.ImageTools.Load(FakeScan());
        var r = PrintHub.Core.Imaging.ImageTools.DetectDocumentBounds(bmp);
        Assert.NotNull(r);
        Assert.InRange(r!.Value.X, 0.08f, 0.14f);   // 150/1200 = 0.125
        Assert.InRange(r.Value.W, 0.70f, 0.80f);    // 900/1200 = 0.75
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-2.5)]
    public void Skew_is_detected(double deg)
    {
        using var bmp = PrintHub.Core.Imaging.ImageTools.Load(FakeScan(deg));
        var found = PrintHub.Core.Imaging.ImageTools.DetectDocumentSkew(bmp);
        Assert.InRange(found, deg - 0.7, deg + 0.7);
    }

    [Fact]
    public void Render_applies_rotation_crop_and_filter()
    {
        var edits = new PrintHub.Core.Imaging.PageEdits { Rotation = 90, Filter = PrintHub.Core.Imaging.PageFilter.BlackAndWhite, Crop = (0, 0, 0.5f, 0.5f) };
        using var bmp = PrintHub.Core.Imaging.ImageTools.Render(FakeScan(), edits);
        Assert.Equal(800, bmp.Width);   // 1600/2 after rotation
        Assert.Equal(600, bmp.Height);
        var px = bmp.GetPixel(10, 10);
        Assert.True(px.R is 0 or 255);
    }

    [Fact]
    public void Pdf_is_structurally_valid_and_xref_offsets_are_exact()
    {
        var jpeg = PrintHub.Core.Imaging.ImageTools.Encode(PrintHub.Core.Imaging.ImageTools.Load(FakeScan()), PrintHub.Core.Imaging.OutputFormat.Jpeg);
        var ocr = new PrintHub.Core.Imaging.OcrResult { Width = 1200, Height = 1600, Lines = new[] { new PrintHub.Core.Imaging.OcrLine("Hello (world)", new[] { new PrintHub.Core.Imaging.OcrWord("Hello", 200, 260, 100, 20), new PrintHub.Core.Imaging.OcrWord("(world)", 320, 260, 120, 20) }) } };
        var pdf = PrintHub.Core.Imaging.PdfWriter.Build(new[] { new PrintHub.Core.Imaging.PdfPageData(jpeg, 1200, 1600, 300, ocr), new PrintHub.Core.Imaging.PdfPageData(jpeg, 1200, 1600, 300) });
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/Count 2", text);
        Assert.Contains(@"\(world\)", text);
        // every xref entry must point exactly at "<n> 0 obj"
        int xrefPos = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        long xref = long.Parse(text[(xrefPos + 10)..].Split('\n')[0]);
        var lines = text[(int)xref..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]);
        for (int i = 1; i < count; i++)
        {
            long off = long.Parse(lines[2 + i][..10]);
            Assert.StartsWith($"{i} 0 obj", text[(int)off..]);
        }
    }

    [Fact]
    public void Multipage_tiff_roundtrips()
    {
        using var a = PrintHub.Core.Imaging.ImageTools.Load(FakeScan());
        using var b = PrintHub.Core.Imaging.ImageTools.Load(FakeScan());
        var bytes = PrintHub.Core.Imaging.ImageTools.EncodeMultiPageTiff(new[] { a, b });
        using var ms = new MemoryStream(bytes);
        using var img = System.Drawing.Image.FromStream(ms);
        Assert.Equal(2, img.GetFrameCount(System.Drawing.Imaging.FrameDimension.Page));
    }
}

public class PrintingTests
{
    [Fact]
    public async Task Our_pdf_opens_in_the_Windows_pdf_engine_and_renders_pages()
    {
        using var bmp = new System.Drawing.Bitmap(800, 1100);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.Clear(System.Drawing.Color.White); g.FillEllipse(System.Drawing.Brushes.Red, 100, 100, 400, 400); }
        var jpeg = PrintHub.Core.Imaging.ImageTools.Encode(bmp, PrintHub.Core.Imaging.OutputFormat.Jpeg);
        var pdf = PrintHub.Core.Imaging.PdfWriter.Build(new[]
        {
            new PrintHub.Core.Imaging.PdfPageData(jpeg, 800, 1100, 100),
            new PrintHub.Core.Imaging.PdfPageData(jpeg, 800, 1100, 100, null, 612, 792), // fitted on Letter
        });
        var path = Path.Combine(Path.GetTempPath(), $"printhub-test-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        try
        {
            var src = await PrintHub.Core.Printing.PrintSource.FromFileAsync(path);
            Assert.Equal(2, src.PageCount);
            using var p0 = src.GetPage(0);
            // the red disc must survive PDF -> render
            var c = p0.GetPixel(p0.Width * 300 / 800, p0.Height * 300 / 1100);
            Assert.True(c.R > 200 && c.G < 80, $"expected red, got {c}");
            // page 2 is fitted on Letter (aspect 8.5:11)
            var (w, h) = src.GetSize(1);
            Assert.InRange((double)w / h, 0.76, 0.78);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, 5, "0,1,2,3,4")]
    [InlineData("1-3,5", 5, "0,1,2,4")]
    [InlineData("4-9", 5, "3,4")]
    [InlineData(" 2 ", 5, "1")]
    public void Page_ranges(string? text, int count, string expected) =>
        Assert.Equal(expected, string.Join(",", PrintHub.Core.Printing.PrintOptions.ParseRange(text, count)));

    [Theory]
    [InlineData("abc")]
    [InlineData("9-10")]
    public void Bad_page_ranges_throw(string text) =>
        Assert.Throws<FormatException>(() => PrintHub.Core.Printing.PrintOptions.ParseRange(text, 5));

    [Fact]
    public void Test_page_renders_at_letter_300dpi()
    {
        using var p = PrintHub.Core.Printing.TestPage.Create("Test Printer", "USB");
        Assert.Equal(2550, p.Width);
        Assert.Equal(3300, p.Height);
    }

    [Fact]
    public void Settings_roundtrip_through_json()
    {
        var s = new PrintHub.Core.Settings.AppSettings { PreferUsb = true, SaveFolder = @"D:\Scans" };
        s.Printers.Add(new PrintHub.Core.Settings.SavedPrinter { Id = "x", Name = "HP Test", IppUri = "http://10.0.0.2:631/ipp/print" });
        var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var back = System.Text.Json.JsonSerializer.Deserialize<PrintHub.Core.Settings.AppSettings>(System.Text.Json.JsonSerializer.Serialize(s, opts), opts)!;
        Assert.True(back.PreferUsb);
        Assert.Equal("HP Test", back.Printers[0].Name);
        Assert.Equal(6, back.Shortcuts.Count);
        Assert.NotNull(back.Printers[0].ToDevice().IppUri);
        Assert.Equal(PrintHub.Core.Scanning.PaperSize.IdCard, back.Shortcuts.First(x => x.Name == "Scan ID card").ToScanSettings().Paper);
    }
}

public class EndToEndTests
{
    static byte[] TextPage()
    {
        using var bmp = new System.Drawing.Bitmap(1700, 2200);
        bmp.SetResolution(200, 200);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var f = new System.Drawing.Font("Arial", 56, System.Drawing.GraphicsUnit.Pixel);
            g.DrawString("Quarterly Report", f, System.Drawing.Brushes.Black, 150, 200);
            g.DrawString("Invoice number 20260417 total", f, System.Drawing.Brushes.Black, 150, 340);
        }
        return PrintHub.Core.Imaging.ImageTools.Encode(bmp, PrintHub.Core.Imaging.OutputFormat.Png);
    }

    [Fact]
    public async Task Ocr_reads_words_with_positions()
    {
        if (!PrintHub.Core.Imaging.OcrService.IsAvailable) return; // no OCR language on this machine
        var r = await PrintHub.Core.Imaging.OcrService.RecognizeAsync(TextPage());
        Assert.Contains("Quarterly", r.Text);
        Assert.Contains("20260417", r.Text);
        var w = r.Lines.SelectMany(l => l.Words).First(x => x.Text.StartsWith("Quarterly"));
        Assert.InRange(w.X, 100, 250);
        Assert.InRange(w.Y, 150, 280);
    }

    [Fact]
    public async Task Scan_to_searchable_pdf_end_to_end()
    {
        var folder = Path.Combine(Path.GetTempPath(), "printhub-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var page = new PrintHub.Core.Imaging.ScannedPage(TextPage(), 200);
            page.Edits.Rotation = 0; page.Edits.Filter = PrintHub.Core.Imaging.PageFilter.Enhance;
            var files = await PrintHub.Core.Scanning.ExportService.SaveAsync(new[] { page, page }, PrintHub.Core.Imaging.OutputFormat.Pdf, folder, "e2e", searchable: true);
            var file = Assert.Single(files);
            var bytes = await File.ReadAllBytesAsync(file);
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            Assert.Contains("/Count 2", text);
            if (PrintHub.Core.Imaging.OcrService.IsAvailable) Assert.Contains("Quarterly", text); // invisible text layer

            // second save must not overwrite
            var again = await PrintHub.Core.Scanning.ExportService.SaveAsync(new[] { page }, PrintHub.Core.Imaging.OutputFormat.Pdf, folder, "e2e");
            Assert.NotEqual(file, again[0]);

            // Windows' own engine can open it
            var src = await PrintHub.Core.Printing.PrintSource.FromFileAsync(file);
            Assert.Equal(2, src.PageCount);

            // images: one file per page, plus multi-page TIFF as one file
            var jpgs = await PrintHub.Core.Scanning.ExportService.SaveAsync(new[] { page, page }, PrintHub.Core.Imaging.OutputFormat.Jpeg, folder, "pic");
            Assert.Equal(2, jpgs.Count);
            var tif = await PrintHub.Core.Scanning.ExportService.SaveAsync(new[] { page, page }, PrintHub.Core.Imaging.OutputFormat.Tiff, folder, "multi");
            Assert.Single(tif);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
