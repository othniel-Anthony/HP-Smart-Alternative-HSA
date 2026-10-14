using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace PrintHub.Core.Imaging;

/// <summary>
/// PWG Raster (PWG 5102.4, "image/pwg-raster"): the page as plain pixels, the format driverless (AirPrint, IPP Everywhere) printers take natively.
/// A JPEG makes the printer decode and treat the page as a photo; raster is what it prints from directly. One page, 8 bits per colour, sRGB, chunky.
/// </summary>
public static class PwgRaster
{
    public const int HeaderSize = 1796;

    /// <summary>One page: see <see cref="EncodePages"/>.</summary>
    public static byte[] Encode(Bitmap page, double widthPt, double heightPt, int dpi, string pageSizeName, int copies = 1, bool grayscale = false, double marginInches = 0.12, int repeat = 1) =>
        EncodePages(new[] { page }, widthPt, heightPt, dpi, pageSizeName, copies, grayscale, marginInches, repeat);

    /// <summary>
    /// The pages fitted into sheets of <paramref name="widthPt"/> x <paramref name="heightPt"/> points (1/72 inch), centred on white, at <paramref name="dpi"/>,
    /// as one PWG raster document. A page that is wider than high is turned to fill a portrait sheet.
    /// </summary>
    public static byte[] EncodePages(IReadOnlyList<Bitmap> pages, double widthPt, double heightPt, int dpi, string pageSizeName, int copies = 1, bool grayscale = false, double marginInches = 0.12,
        int repeat = 1)
    {
        int w = (int)Math.Round(widthPt / 72.0 * dpi), h = (int)Math.Round(heightPt / 72.0 * dpi);
        int bpp = grayscale ? 1 : 3;
        repeat = Math.Max(1, repeat);
        var records = new List<byte[]>();      // one entry per page: its header and pixels
        foreach (var page in pages)
        {
            using var canvas = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            canvas.SetResolution(dpi, dpi);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                bool turn = page.Width > page.Height && h > w;
                float pw = turn ? page.Height : page.Width, ph = turn ? page.Width : page.Height;
                float m = (float)(marginInches * dpi);
                float s = Math.Min((w - 2 * m) / pw, (h - 2 * m) / ph);
                float dw = pw * s, dh = ph * s;
                if (turn)
                {
                    g.TranslateTransform(w / 2f, h / 2f); g.RotateTransform(90);
                    g.DrawImage(page, new RectangleF(-dh / 2, -dw / 2, dh, dw));   // drawn rotated: the sheet's width is the page's height
                }
                else g.DrawImage(page, new RectangleF((w - dw) / 2, (h - dh) / 2, dw, dh));
            }
            using var rec = new MemoryStream(w * h / 8);
            rec.Write(Header(w, h, bpp, dpi, widthPt, heightPt, pageSizeName, copies, grayscale, pages.Count * repeat));
            WritePage(rec, canvas, bpp, grayscale);
            records.Add(rec.ToArray());
        }
        // "repeat" writes the whole set again and again: the copies are in the document, for printers that ignore a copies request for raster
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("RaS2"));
        for (int r = 0; r < repeat; r++)
            foreach (var rec in records) ms.Write(rec);
        return ms.ToArray();
    }

    static void WritePage(Stream ms, Bitmap canvas, int bpp, bool grayscale)
    {
        int w = canvas.Width, h = canvas.Height;
        var data = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var line = new byte[w * bpp];
            var prev = new byte[w * bpp]; bool havePrev = false; int repeat = 0;
            var raw = new byte[Math.Abs(data.Stride)];
            var packed = new MemoryStream();
            void Flush()
            {
                if (!havePrev) return;
                ms.WriteByte((byte)repeat);          // how many more times this line appears
                ms.Write(packed.GetBuffer(), 0, (int)packed.Length);
            }
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, raw, 0, raw.Length);
                for (int x = 0; x < w; x++)
                {
                    byte b = raw[x * 3], g = raw[x * 3 + 1], r = raw[x * 3 + 2];
                    if (grayscale) line[x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    else { line[x * 3] = r; line[x * 3 + 1] = g; line[x * 3 + 2] = b; }
                }
                if (havePrev && repeat < 255 && line.AsSpan().SequenceEqual(prev)) { repeat++; continue; }
                Flush();
                packed.SetLength(0);
                PackLine(line, bpp, packed);
                Buffer.BlockCopy(line, 0, prev, 0, line.Length); havePrev = true; repeat = 0;
            }
            Flush();
        }
        finally { canvas.UnlockBits(data); }
    }

    /// <summary>PackBits on whole pixels: a control byte c in 0..127 repeats the next pixel c+1 times; 129..255 is followed by 257-c different pixels.</summary>
    static void PackLine(byte[] line, int bpp, Stream o)
    {
        int n = line.Length / bpp, i = 0;
        bool Same(int a, int b) { for (int k = 0; k < bpp; k++) if (line[a * bpp + k] != line[b * bpp + k]) return false; return true; }
        while (i < n)
        {
            int run = 1;
            while (i + run < n && run < 128 && Same(i, i + run)) run++;
            if (run > 1) { o.WriteByte((byte)(run - 1)); o.Write(line, i * bpp, bpp); i += run; continue; }
            int lit = 1;
            while (i + lit < n && lit < 128 && !(i + lit + 1 < n && Same(i + lit, i + lit + 1))) lit++;
            o.WriteByte((byte)(257 - lit)); o.Write(line, i * bpp, lit * bpp); i += lit;
        }
    }

    static byte[] Header(int w, int h, int bpp, int dpi, double widthPt, double heightPt, string pageSizeName, int copies, bool gray, int totalPages = 1)
    {
        var b = new byte[HeaderSize];
        void Str(int at, string s) { var t = Encoding.ASCII.GetBytes(s); Array.Copy(t, 0, b, at, Math.Min(t.Length, 63)); }
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
        Str(0, "PwgRaster");
        U32(276, (uint)dpi); U32(280, (uint)dpi);                    // HWResolution
        U32(340, (uint)Math.Max(1, copies));                         // NumCopies
        U32(352, (uint)Math.Round(widthPt)); U32(356, (uint)Math.Round(heightPt));   // PageSize in points
        U32(372, (uint)w); U32(376, (uint)h);                        // Width, Height
        U32(384, 8);                                                 // BitsPerColor
        U32(388, (uint)(8 * bpp));                                   // BitsPerPixel
        U32(392, (uint)(w * bpp));                                   // BytesPerLine
        U32(396, 0);                                                 // ColorOrder: chunky
        U32(400, gray ? 18u : 19u);                                  // ColorSpace: sGray / sRGB
        U32(420, (uint)bpp);                                         // NumColors
        U32(452, (uint)totalPages);                                  // TotalPageCount
        U32(456, 1); U32(460, 1);                                    // CrossFeedTransform, FeedTransform
        U32(464, 0); U32(468, 0); U32(472, (uint)w); U32(476, (uint)h);   // ImageBox
        U32(480, 0xFFFFFF);                                          // AlternatePrimary
        Str(1668, "auto");                                           // RenderingIntent
        Str(1732, pageSizeName);                                     // PageSizeName
        return b;
    }

    /// <summary>The number of pages in a document made by <see cref="EncodePages"/> (for tests and logs).</summary>
    public static int CountPages(byte[] doc)
    {
        if (doc.Length < 4 || Encoding.ASCII.GetString(doc, 0, 4) != "RaS2") throw new InvalidDataException("not a PWG raster document");
        int p = 4, pages = 0;
        while (p + HeaderSize <= doc.Length)
        {
            int w = (int)BinaryPrimitives.ReadUInt32BigEndian(doc.AsSpan(p + 372)), h = (int)BinaryPrimitives.ReadUInt32BigEndian(doc.AsSpan(p + 376));
            int bpp = (int)BinaryPrimitives.ReadUInt32BigEndian(doc.AsSpan(p + 388)) / 8;
            p += HeaderSize;
            for (int y = 0; y < h;)
            {
                int repeat = doc[p++] + 1;
                for (int x = 0; x < w;)
                {
                    int c = doc[p++];
                    if (c < 128) { x += c + 1; p += bpp; }
                    else { int n = 257 - c; x += n; p += n * bpp; }
                }
                y += repeat;
            }
            pages++;
        }
        return pages;
    }

    /// <summary>Reads a document made by <see cref="Encode"/> back (for tests): width, height, bytes per pixel and the pixels as RGB (or gray) rows.</summary>
    public static (int Width, int Height, int BytesPerPixel, byte[] Pixels, uint ColorSpace, int Dpi, string PageSize) Decode(byte[] doc)
    {
        if (Encoding.ASCII.GetString(doc, 0, 4) != "RaS2") throw new InvalidDataException("not a PWG raster document");
        int baseAt = 4;
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(doc.AsSpan(baseAt + at));
        int w = (int)U32(372), h = (int)U32(376), bpp = (int)U32(388) / 8;
        var pix = new byte[w * h * bpp];
        int p = 4 + HeaderSize, y = 0;
        while (y < h)
        {
            int repeat = doc[p++] + 1;
            var line = new byte[w * bpp]; int x = 0;
            while (x < w)
            {
                int c = doc[p++];
                if (c < 128) { int n = c + 1; for (int k = 0; k < n; k++) Array.Copy(doc, p, line, (x + k) * bpp, bpp); p += bpp; x += n; }
                else { int n = 257 - c; Array.Copy(doc, p, line, x * bpp, n * bpp); p += n * bpp; x += n; }
            }
            for (int r = 0; r < repeat && y < h; r++, y++) Array.Copy(line, 0, pix, y * w * bpp, w * bpp);
        }
        var nameBytes = doc.AsSpan(baseAt + 1732, 64).ToArray();
        return (w, h, bpp, pix, U32(400), (int)U32(276), Encoding.ASCII.GetString(nameBytes).TrimEnd('\0'));
    }
}
