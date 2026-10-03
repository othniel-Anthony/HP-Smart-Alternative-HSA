using System.Globalization;
using System.Text;

namespace PrintHub.Core.Imaging;

/// <param name="PageWidthPt">When set (with <paramref name="PageHeightPt"/>), the image is fitted and centred on a page of this size instead of filling it.</param>
public sealed record PdfPageData(byte[] Jpeg, int WidthPx, int HeightPx, int Dpi, OcrResult? Ocr = null, double? PageWidthPt = null, double? PageHeightPt = null);

/// <summary>Dependency-free PDF writer: one JPEG per page, optional invisible OCR text layer so the PDF is searchable.</summary>
public static class PdfWriter
{
    public static byte[] Build(IReadOnlyList<PdfPageData> pages, string title = "Scan")
    {
        if (pages.Count == 0) throw new ArgumentException("No pages to write.");
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Begin(int id) { offsets.Add(ms.Position); Raw($"{id} 0 obj\n"); }
        string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        Raw("%PDF-1.4\n%âãÏÓ\n");

        // object ids: 1 catalog, 2 pages, 3 font, 4 info, then 3 per page (page, content, image)
        int First(int i) => 5 + i * 3;
        Begin(1); Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2); Raw($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{First(i)} 0 R"))}] >>\nendobj\n");
        Begin(3); Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Begin(4); Raw($"<< /Title ({Escape(title)}) /Producer (HSA) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>\nendobj\n");

        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            double k = 72.0 / Math.Max(p.Dpi, 1);
            double wPt = p.WidthPx * k, hPt = p.HeightPx * k;
            double pageW = wPt, pageH = hPt, drawW = wPt, drawH = hPt, drawX = 0, drawY = 0;
            if (p.PageWidthPt is { } pw && p.PageHeightPt is { } ph)
            {
                const double margin = 18;
                double fit = Math.Min((pw - 2 * margin) / wPt, (ph - 2 * margin) / hPt);
                drawW = wPt * fit; drawH = hPt * fit;
                drawX = (pw - drawW) / 2; drawY = (ph - drawH) / 2;
                pageW = pw; pageH = ph; wPt = drawW; hPt = drawH;
            }

            var content = new StringBuilder();
            content.Append($"q {N(drawW)} 0 0 {N(drawH)} {N(drawX)} {N(drawY)} cm /Im0 Do Q\n");
            if (p.Ocr is { } ocr && ocr.Width > 0)
            {
                double sx = wPt / ocr.Width, sy = hPt / ocr.Height;
                content.Append("BT 3 Tr\n"); // render mode 3 = invisible
                foreach (var word in ocr.Lines.SelectMany(l => l.Words))
                {
                    var text = Sanitize(word.Text);
                    if (text.Length == 0) continue;
                    double size = Math.Max(2, word.H * sy * 0.85);
                    double natural = 0.5 * size * text.Length;
                    double tz = Math.Clamp(word.W * sx / natural * 100, 10, 400);
                    double x = word.X * sx, y = hPt - (word.Y + word.H * 0.8) * sy;
                    content.Append($"/F1 {N(size)} Tf {N(tz)} Tz 1 0 0 1 {N(x)} {N(y)} Tm ({Escape(text)}) Tj\n");
                }
                content.Append("ET\n");
            }
            var contentBytes = Encoding.Latin1.GetBytes(content.ToString());

            int page = First(i), cont = page + 1, img = page + 2;
            Begin(page);
            Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pageW)} {N(pageH)}] /Resources << /XObject << /Im0 {img} 0 R >> /Font << /F1 3 0 R >> >> /Contents {cont} 0 R >>\nendobj\n");
            Begin(cont);
            Raw($"<< /Length {contentBytes.Length} >>\nstream\n"); ms.Write(contentBytes); Raw("\nendstream\nendobj\n");
            Begin(img);
            Raw($"<< /Type /XObject /Subtype /Image /Width {p.WidthPx} /Height {p.HeightPx} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            ms.Write(p.Jpeg); Raw("\nendstream\nendobj\n");
        }

        long xref = ms.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Raw($"{o:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    static string Sanitize(string s) => new(s.Select(c => c is >= ' ' and <= 'ÿ' ? c : '?').ToArray());

    static string Escape(string s) => Sanitize(s).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
