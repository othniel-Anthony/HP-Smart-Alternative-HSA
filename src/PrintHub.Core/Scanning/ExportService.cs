using PrintHub.Core.Imaging;

namespace PrintHub.Core.Scanning;

/// <summary>Turns edited pages into files: PDF (optionally searchable), JPEG, PNG, TIFF, BMP.</summary>
public static class ExportService
{
    public static string SuggestName(string prefix = "Scan") => $"{prefix} {DateTime.Now:yyyy-MM-dd HH.mm.ss}";

    public static string DefaultFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(docs, "HSA Scans");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Render all pages to final bitmaps (apply edits). Caller disposes.</summary>
    public static List<System.Drawing.Bitmap> RenderAll(IEnumerable<ScannedPage> pages) => pages.Select(p => ImageTools.Render(p)).ToList();

    public static byte[] BuildPdf(IReadOnlyList<ScannedPage> pages, IReadOnlyList<OcrResult?>? ocr = null, string title = "Scan")
    {
        var data = new List<PdfPageData>();
        for (int i = 0; i < pages.Count; i++)
        {
            using var bmp = ImageTools.Render(pages[i]);
            data.Add(new PdfPageData(ImageTools.Encode(bmp, OutputFormat.Jpeg, 85), bmp.Width, bmp.Height, pages[i].Dpi, ocr?[i]));
        }
        return PdfWriter.Build(data, title);
    }

    /// <summary>Text of one page as the user sees it, turned upright first if it was scanned sideways.</summary>
    public static async Task<(OcrResult Result, int Rotation)> ReadPageTextAsync(ScannedPage page, CancellationToken ct = default)
    {
        using var bmp = ImageTools.Render(page);
        return await OcrService.RecognizeUprightAsync(ImageTools.Encode(bmp, OutputFormat.Png), ct);
    }

    /// <summary>
    /// OCR for a searchable PDF. A page that only reads correctly when turned (scanned sideways) is exported turned upright,
    /// so the text layer lines up with what is printed. The user's own page edits are not changed.
    /// </summary>
    static async Task<(IReadOnlyList<ScannedPage> Pages, IReadOnlyList<OcrResult?> Ocr)> ReadAndStraightenAsync(
        IReadOnlyList<ScannedPage> pages, IProgress<int>? progress, CancellationToken ct)
    {
        var outPages = new List<ScannedPage>(); var results = new List<OcrResult?>();
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (result, turn) = await ReadPageTextAsync(pages[i], ct);
            var page = pages[i];
            if (turn != 0 && OcrService.WordStats(result).Good >= 5)
            {
                page = new ScannedPage(pages[i].Original, pages[i].Dpi) { Edits = pages[i].Edits.Clone(), SourceName = pages[i].SourceName };
                page.Edits.ExtraTurn = (page.Edits.ExtraTurn + turn) % 360;
            }
            else if (turn != 0) { result = (await OcrService.RecognizeAsync(EncodeForOcr(pages[i]), ct)); } // not convincing: keep the page as it is
            outPages.Add(page); results.Add(result);
            progress?.Report(i + 1);
        }
        return (outPages, results);
    }

    static byte[] EncodeForOcr(ScannedPage page) { using var bmp = ImageTools.Render(page); return ImageTools.Encode(bmp, OutputFormat.Png); }

    public static async Task<List<OcrResult?>> RunOcrAsync(IReadOnlyList<ScannedPage> pages, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var results = new List<OcrResult?>();
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var bmp = ImageTools.Render(pages[i]);
            results.Add(await OcrService.RecognizeAsync(ImageTools.Encode(bmp, OutputFormat.Png), ct));
            progress?.Report(i + 1);
        }
        return results;
    }

    /// <summary>Write pages to <paramref name="folder"/>. Returns the created paths.</summary>
    public static async Task<List<string>> SaveAsync(IReadOnlyList<ScannedPage> pages, OutputFormat format, string folder, string baseName,
        bool searchable = false, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (pages.Count == 0) throw new ArgumentException("There are no pages to save.");
        Directory.CreateDirectory(folder);
        baseName = string.Concat(baseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var files = new List<string>();
        string Unique(string stem, string ext)
        {
            var path = Path.Combine(folder, stem + ext);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
            return path;
        }

        if (format == OutputFormat.Pdf)
        {
            IReadOnlyList<OcrResult?>? ocr = null;
            var pdfPages = pages;
            if (searchable && OcrService.IsAvailable) (pdfPages, ocr) = await ReadAndStraightenAsync(pages, progress, ct);
            var pdf = await Task.Run(() => BuildPdf(pdfPages, ocr, baseName), ct);
            var path = Unique(baseName, ".pdf");
            await File.WriteAllBytesAsync(path, pdf, ct);
            files.Add(path);
        }
        else if (format == OutputFormat.Tiff && pages.Count > 1)
        {
            var bytes = await Task.Run(() => { var bmps = RenderAll(pages); try { return ImageTools.EncodeMultiPageTiff(bmps); } finally { bmps.ForEach(b => b.Dispose()); } }, ct);
            var path = Unique(baseName, ".tif");
            await File.WriteAllBytesAsync(path, bytes, ct);
            files.Add(path);
        }
        else
        {
            for (int i = 0; i < pages.Count; i++)
            {
                var i0 = i;
                var bytes = await Task.Run(() => { using var bmp = ImageTools.Render(pages[i0]); return ImageTools.Encode(bmp, format); }, ct);
                var stem = pages.Count > 1 ? $"{baseName} - {i + 1}" : baseName;
                var path = Unique(stem, ImageTools.Extension(format));
                await File.WriteAllBytesAsync(path, bytes, ct);
                files.Add(path);
                progress?.Report(i + 1);
            }
        }
        return files;
    }
}
