using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PrintHub.Core.Imaging;

public enum OutputFormat { Jpeg, Png, Tiff, Bmp, Pdf }

/// <summary>Image processing on top of GDI+. All methods are safe to call from any thread.</summary>
public static class ImageTools
{
    public static Bitmap Load(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var img = Image.FromStream(ms);
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
        float dx = img.HorizontalResolution > 0 ? img.HorizontalResolution : 96, dy = img.VerticalResolution > 0 ? img.VerticalResolution : 96;
        bmp.SetResolution(dx, dy);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
        return bmp;
    }

    public static (int W, int H) GetSize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var img = Image.FromStream(ms, false, false);
        return (img.Width, img.Height);
    }

    public static Bitmap Resize(Bitmap src, int maxDim)
    {
        if (maxDim <= 0 || Math.Max(src.Width, src.Height) <= maxDim) return (Bitmap)src.Clone();
        double s = (double)maxDim / Math.Max(src.Width, src.Height);
        int w = Math.Max(1, (int)(src.Width * s)), h = Math.Max(1, (int)(src.Height * s));
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }

    // ------------------------------------------------------------------ render pipeline

    /// <summary>Apply the page's edits. <paramref name="maxDim"/> &gt; 0 renders a reduced preview.</summary>
    public static Bitmap Render(ScannedPage page, int maxDim = 0) => Render(page.Original, page.Edits, maxDim);

    public static Bitmap Render(byte[] original, PageEdits e, int maxDim = 0)
    {
        using var full = Load(original);
        Bitmap cur = maxDim > 0 ? Resize(full, maxDim * 2) : (Bitmap)full.Clone(); // keep headroom so crops stay sharp

        Bitmap Step(Bitmap next) { cur.Dispose(); return next; }

        if (Math.Abs(e.Straighten) > 0.01) cur = Step(RotateArbitrary(cur, e.Straighten));
        cur = Step(RotateQuarter(cur, e.Rotation));
        if (e.Crop is { } c) cur = Step(CropFraction(cur, c.X, c.Y, c.W, c.H));
        if (maxDim > 0) cur = Step(Resize(cur, maxDim));

        cur = Step(e.Filter switch
        {
            PageFilter.Enhance => AutoLevels(cur),
            PageFilter.Grayscale => Grayscale(cur),
            PageFilter.BlackAndWhite => Threshold(cur),
            _ => (Bitmap)cur.Clone(),
        });
        if (e.Brightness != 0 || e.Contrast != 0) cur = Step(AdjustBrightnessContrast(cur, e.Brightness, e.Contrast));
        if (e.Texts.Count > 0 || e.Images.Count > 0) DrawAnnotations(cur, e);
        if (e.ExtraTurn % 360 != 0) cur = Step(RotateQuarter(cur, e.ExtraTurn)); // last, so annotations turn with the page
        return cur;
    }

    public static byte[] Encode(Bitmap bmp, OutputFormat fmt, int jpegQuality = 90)
    {
        using var ms = new MemoryStream();
        switch (fmt)
        {
            case OutputFormat.Png: bmp.Save(ms, ImageFormat.Png); break;
            case OutputFormat.Tiff: bmp.Save(ms, ImageFormat.Tiff); break;
            case OutputFormat.Bmp: bmp.Save(ms, ImageFormat.Bmp); break;
            default:
            {
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ps = new EncoderParameters(1);
                ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
                bmp.Save(ms, enc, ps);
                break;
            }
        }
        return ms.ToArray();
    }

    public static string Extension(OutputFormat f) => f switch { OutputFormat.Png => ".png", OutputFormat.Tiff => ".tif", OutputFormat.Bmp => ".bmp", OutputFormat.Pdf => ".pdf", _ => ".jpg" };

    /// <summary>Save pages as a multi-page TIFF.</summary>
    public static byte[] EncodeMultiPageTiff(IEnumerable<Bitmap> pages)
    {
        var list = pages.ToList();
        if (list.Count == 0) throw new ArgumentException("No pages");
        var tiffEnc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid);
        using var ms = new MemoryStream();
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
        list[0].Save(ms, tiffEnc, p);
        for (int i = 1; i < list.Count; i++)
        {
            using var pp = new EncoderParameters(1);
            pp.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            list[0].SaveAdd(list[i], pp);
        }
        using var fin = new EncoderParameters(1);
        fin.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
        list[0].SaveAdd(fin);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ geometry

    public static Bitmap RotateQuarter(Bitmap src, int degrees)
    {
        var b = (Bitmap)src.Clone();
        switch (((degrees % 360) + 360) % 360)
        {
            case 90: b.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 180: b.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 270: b.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
        return b;
    }

    /// <summary>Average colour of the four corners: the background to fill with when a rotation exposes new corners.</summary>
    static Color CornerColor(Bitmap src)
    {
        int n = Math.Max(1, Math.Min(Math.Min(src.Width, src.Height) / 50, 12));
        long r = 0, g = 0, b = 0, count = 0;
        foreach (var (x0, y0) in new[] { (0, 0), (src.Width - n, 0), (0, src.Height - n), (src.Width - n, src.Height - n) })
            for (int y = y0; y < y0 + n; y++)
                for (int x = x0; x < x0 + n; x++) { var c = src.GetPixel(x, y); r += c.R; g += c.G; b += c.B; count++; }
        return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
    }

    public static Bitmap RotateArbitrary(Bitmap src, double degrees)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        using var g = Graphics.FromImage(dst);
        g.Clear(CornerColor(src));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TranslateTransform(src.Width / 2f, src.Height / 2f);
        g.RotateTransform((float)degrees);
        g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
        return dst;
    }

    public static Bitmap CropFraction(Bitmap src, float x, float y, float w, float h)
    {
        int px = Math.Clamp((int)Math.Round(x * src.Width), 0, src.Width - 1), py = Math.Clamp((int)Math.Round(y * src.Height), 0, src.Height - 1);
        int pw = Math.Clamp((int)Math.Round(w * src.Width), 1, src.Width - px), ph = Math.Clamp((int)Math.Round(h * src.Height), 1, src.Height - py);
        var dst = new Bitmap(pw, ph, PixelFormat.Format24bppRgb);
        dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, pw, ph), new Rectangle(px, py, pw, ph), GraphicsUnit.Pixel);
        return dst;
    }

    // ------------------------------------------------------------------ pixel helpers

    static byte[] ReadPixels(Bitmap b, out int stride, out BitmapData data)
    {
        data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        stride = data.Stride;
        var buf = new byte[Math.Abs(stride) * b.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        b.UnlockBits(data);
        return buf;
    }

    static void WritePixels(Bitmap b, byte[] buf)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, d.Scan0, buf.Length);
        b.UnlockBits(d);
    }

    static int[] LuminanceHistogram(byte[] px, int w, int h, int stride)
    {
        var hist = new int[256];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 3;
                hist[(px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000]++;
            }
        return hist;
    }

    static int Otsu(int[] hist, long total)
    {
        double sum = 0; for (int i = 0; i < 256; i++) sum += i * hist[i];
        double sumB = 0, best = 0; long wB = 0; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            long wF = total - wB; if (wF == 0) break;
            sumB += t * hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF, between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; thr = t; }
        }
        return thr;
    }

    // ------------------------------------------------------------------ filters

    public static Bitmap Grayscale(Bitmap src) => ApplyMatrix(src, new ColorMatrix(new[]
    {
        new[] { .299f, .299f, .299f, 0, 0 }, new[] { .587f, .587f, .587f, 0, 0 }, new[] { .114f, .114f, .114f, 0, 0 },
        new[] { 0f, 0, 0, 1, 0 }, new[] { 0f, 0, 0, 0, 1 },
    }));

    public static Bitmap AdjustBrightnessContrast(Bitmap src, int brightness, int contrast)
    {
        float s = (100 + contrast) / 100f;
        float t = 0.5f * (1 - s) + brightness / 200f;
        return ApplyMatrix(src, new ColorMatrix(new[]
        {
            new[] { s, 0, 0, 0, 0 }, new[] { 0, s, 0, 0, 0 }, new[] { 0, 0, s, 0, 0 },
            new[] { 0f, 0, 0, 1, 0 }, new[] { t, t, t, 0, 1 },
        }));
    }

    static Bitmap ApplyMatrix(Bitmap src, ColorMatrix m)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        using var g = Graphics.FromImage(dst);
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(m);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return dst;
    }

    /// <summary>Stretch levels so paper becomes white and ink stays dark (2nd..98th luminance percentile).</summary>
    public static Bitmap AutoLevels(Bitmap src)
    {
        var px = ReadPixels(src, out int stride, out _);
        var hist = LuminanceHistogram(px, src.Width, src.Height, stride);
        long total = (long)src.Width * src.Height;
        long acc = 0; int lo = 0, hi = 255;
        for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= total * 0.02) { lo = i; break; } }
        acc = 0;
        for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= total * 0.25) { hi = i; break; } } // paper dominates: take the 75th percentile as white
        if (hi - lo < 20) { lo = 0; hi = 255; }
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp((i - lo) * 255 / Math.Max(1, hi - lo), 0, 255);
        for (int i = 0; i < px.Length; i++) px[i] = lut[px[i]];
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        WritePixels(dst, px);
        return dst;
    }

    public static Bitmap Threshold(Bitmap src, int? level = null)
    {
        var px = ReadPixels(src, out int stride, out _);
        int thr = level ?? Otsu(LuminanceHistogram(px, src.Width, src.Height, stride), (long)src.Width * src.Height);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int i = y * stride + x * 3;
                byte v = (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000 > thr ? (byte)255 : (byte)0;
                px[i] = px[i + 1] = px[i + 2] = v;
            }
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        WritePixels(dst, px);
        return dst;
    }

    // ------------------------------------------------------------------ analysis

    /// <summary>Find the document inside the scan bed. Returns fractions (x, y, w, h) or null when the whole image looks like content.</summary>
    public static (float X, float Y, float W, float H)? DetectDocumentBounds(Bitmap src)
    {
        using var small = Resize(src, 500);
        var px = ReadPixels(small, out int stride, out _);
        int w = small.Width, h = small.Height;
        int Lum(int x, int y) { int i = y * stride + x * 3; return (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000; }

        // background = median luminance of the outer 3 px frame
        var border = new List<int>();
        for (int x = 0; x < w; x++) { border.Add(Lum(x, 0)); border.Add(Lum(x, h - 1)); }
        for (int y = 0; y < h; y++) { border.Add(Lum(0, y)); border.Add(Lum(w - 1, y)); }
        border.Sort();
        int bg = border[border.Count / 2];

        var rows = new int[h]; var cols = new int[w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (Math.Abs(Lum(x, y) - bg) > 30) { rows[y]++; cols[x]++; }

        int top = Array.FindIndex(rows, r => r > w * 0.01), bottom = Array.FindLastIndex(rows, r => r > w * 0.01);
        int left = Array.FindIndex(cols, c => c > h * 0.01), right = Array.FindLastIndex(cols, c => c > h * 0.01);
        if (top < 0 || left < 0 || bottom <= top || right <= left) return null;

        float pad = 0.005f;
        float fx = Math.Max(0, left / (float)w - pad), fy = Math.Max(0, top / (float)h - pad);
        float fw = Math.Min(1 - fx, (right + 1) / (float)w - fx + pad), fh = Math.Min(1 - fy, (bottom + 1) / (float)h - fy + pad);
        if (fw * fh < 0.1f || fw * fh > 0.98f) return null;
        return (fx, fy, fw, fh);
    }

    /// <summary>Skew of the document on a scanner bed: looks only inside the detected page so the lid/bed edges don't dominate.</summary>
    public static double DetectDocumentSkew(Bitmap src)
    {
        var b = DetectDocumentBounds(src);
        using var region = b is { } r
            ? CropFraction(src, r.X + r.W * 0.08f, r.Y + r.H * 0.08f, r.W * 0.84f, r.H * 0.84f)
            : CropFraction(src, 0.06f, 0.06f, 0.88f, 0.88f);
        return DetectSkew(region);
    }

    /// <summary>Estimate text skew in degrees (positive = lines run downward to the right). Returns 0 if uncertain.</summary>
    public static double DetectSkew(Bitmap src)
    {
        using var small = Resize(src, 600);
        var px = ReadPixels(small, out int stride, out _);
        int w = small.Width, h = small.Height;
        int thr = Otsu(LuminanceHistogram(px, w, h, stride), (long)w * h);
        var dark = new List<(int X, int Y)>();
        for (int y = 0; y < h; y += 1)
            for (int x = 0; x < w; x += 1)
            {
                int i = y * stride + x * 3;
                if ((px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000 < thr) dark.Add((x, y));
            }
        if (dark.Count < 200 || dark.Count > w * h * 0.5) return 0;
        if (dark.Count > 60000) dark = dark.Where((_, idx) => idx % (dark.Count / 60000 + 1) == 0).ToList();

        double Score(double deg)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            int bins = h * 2; var hist = new int[bins];
            foreach (var (x, y) in dark)
            {
                int b = (int)(y * c - x * s + w);
                if (b >= 0 && b < bins) hist[b]++;
            }
            double sum = 0; foreach (var v in hist) sum += (double)v * v;
            return sum;
        }

        double best = 0, bestScore = Score(0), zeroScore = bestScore;
        for (double a = -6; a <= 6; a += 0.5) { var sc = Score(a); if (sc > bestScore) { bestScore = sc; best = a; } }
        double refined = best;
        for (double a = best - 0.5; a <= best + 0.5; a += 0.1) { var sc = Score(a); if (sc > bestScore) { bestScore = sc; refined = a; } }
        return bestScore > zeroScore * 1.03 ? Math.Round(refined, 1) : 0;
    }

    // ------------------------------------------------------------------ annotations

    static void DrawAnnotations(Bitmap bmp, PageEdits e)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        foreach (var t in e.Texts)
        {
            float size = Math.Max(6, t.SizeFraction * bmp.Height);
            using var font = new Font("Segoe UI", size, t.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ColorTranslator.FromHtml(t.ColorHex));
            g.DrawString(t.Text, font, brush, t.X * bmp.Width, t.Y * bmp.Height);
        }
        foreach (var im in e.Images)
        {
            using var ms = new MemoryStream(im.Png);
            using var img = Image.FromStream(ms);
            float w = im.WidthFraction * bmp.Width, hgt = w * img.Height / img.Width;
            g.DrawImage(img, im.X * bmp.Width, im.Y * bmp.Height, w, hgt);
        }
    }
}
