using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using PrintHub.Core.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PrintHub.Core.Printing;

/// <summary>A document prepared for printing: its pages as encoded images, plus the original file when the printer can take it as is.</summary>
public sealed class PrintSource
{
    readonly List<byte[]> _pages = new();
    readonly List<(int W, int H)> _sizes = new();

    public string Name { get; private init; } = "Document";
    public int PageCount => _pages.Count;
    public int Dpi { get; private init; } = 300;
    /// <summary>Original bytes + MIME type when it is a PDF or JPEG (can be sent to an IPP printer unchanged).</summary>
    public byte[]? OriginalBytes { get; private init; }
    public string? OriginalMime { get; private init; }

    public Bitmap GetPage(int i) => ImageTools.Load(_pages[i]);
    public (int W, int H) GetSize(int i) => _sizes[i];
    public byte[] GetPageBytes(int i) => _pages[i];

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    public static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static PrintSource FromBitmaps(IEnumerable<Bitmap> bitmaps, int dpi, string name = "HSA")
    {
        var src = new PrintSource { Name = name, Dpi = dpi };
        foreach (var b in bitmaps) { src._pages.Add(ImageTools.Encode(b, OutputFormat.Png)); src._sizes.Add((b.Width, b.Height)); }
        return src;
    }

    public static async Task<PrintSource> FromFileAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var bytes = await File.ReadAllBytesAsync(path, ct);

        if (IsPdf(path))
        {
            PdfDocument doc;
            try { doc = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path))).AsTask(ct); }
            catch (Exception ex) { throw new InvalidOperationException("Could not open the PDF (it may be damaged or password protected): " + ex.Message, ex); }

            var src = new PrintSource { Name = name, Dpi = 300, OriginalBytes = bytes, OriginalMime = "application/pdf" };
            for (uint i = 0; i < doc.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = doc.GetPage(i);
                uint width = (uint)Math.Clamp(page.Size.Width / 96.0 * 300, 600, 3600);
                using var ras = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = width, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) }).AsTask(ct);
                ras.Seek(0);
                var buf = new byte[ras.Size];
                await ras.ReadAsync(buf.AsBuffer(), (uint)ras.Size, InputStreamOptions.None).AsTask(ct);
                var (w, h) = ImageTools.GetSize(buf);
                src._pages.Add(buf); src._sizes.Add((w, h));
            }
            return src;
        }

        if (IsImage(path))
        {
            bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            var src = new PrintSource { Name = name, Dpi = 300, OriginalBytes = jpeg ? bytes : null, OriginalMime = jpeg ? "image/jpeg" : null };
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            int frames = 1;
            try { frames = img.GetFrameCount(FrameDimension.Page); } catch { }
            for (int f = 0; f < Math.Max(1, frames); f++)
            {
                if (frames > 1) img.SelectActiveFrame(FrameDimension.Page, f);
                using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
                bmp.SetResolution(img.HorizontalResolution, img.VerticalResolution);
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawImage(img, 0, 0, img.Width, img.Height); }
                src._pages.Add(ImageTools.Encode(bmp, OutputFormat.Png)); src._sizes.Add((bmp.Width, bmp.Height));
            }
            return src;
        }

        throw new NotSupportedException($"'{Path.GetExtension(path)}' files are printed by the app that owns them. Open the file and print from there, or export it as PDF.");
    }
}
