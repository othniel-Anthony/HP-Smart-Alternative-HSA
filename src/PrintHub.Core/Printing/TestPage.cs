using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PrintHub.Core.Printing;

/// <summary>A one-page diagnostic: solid CMYK/RGB patches for clogged-nozzle checks, gradients, a fine grid, text and registration marks.</summary>
public static class TestPage
{
    public const int Dpi = 300;

    public static Bitmap Create(string printerName, string connection, bool color = true)
    {
        const int W = 2550, H = 3300;
        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        bmp.SetResolution(Dpi, Dpi);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var title = new Font("Segoe UI Semibold", 54, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 34, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 24, GraphicsUnit.Pixel);
        g.DrawString("HSA test page", title, Brushes.Black, 150, 130);
        g.DrawString($"{printerName}  ·  {connection}  ·  {DateTime.Now:yyyy-MM-dd HH:mm}", body, Brushes.DimGray, 150, 215);

        // registration marks in all four corners (check margins / alignment)
        using var pen = new Pen(Color.Black, 3);
        foreach (var (x, y) in new[] { (60, 60), (W - 60, 60), (60, H - 60), (W - 60, H - 60) })
        {
            g.DrawLine(pen, x - 40, y, x + 40, y); g.DrawLine(pen, x, y - 40, x, y + 40); g.DrawEllipse(pen, x - 20, y - 20, 40, 40);
        }

        int top = 330;
        g.DrawString("1  Colour patches - every patch should be solid with no streaks or gaps", body, Brushes.Black, 150, top);
        var patches = new (string, Color)[] { ("Black", Color.Black), ("Cyan", Color.FromArgb(0, 174, 239)), ("Magenta", Color.FromArgb(236, 0, 140)), ("Yellow", Color.FromArgb(255, 242, 0)), ("Red", Color.Red), ("Green", Color.FromArgb(0, 166, 81)), ("Blue", Color.Blue) };
        int pw = (W - 300) / patches.Length;
        for (int i = 0; i < patches.Length; i++)
        {
            var c = color || i == 0 ? patches[i].Item2 : Color.FromArgb(patches[i].Item2.GetBrightness() > 0.5 ? 200 : 60, 128, 128, 128);
            using var br = new SolidBrush(color || i == 0 ? c : Grey(patches[i].Item2));
            g.FillRectangle(br, 150 + i * pw, top + 60, pw - 12, 260);
            g.DrawString(patches[i].Item1, small, Brushes.Black, 150 + i * pw, top + 330);
        }

        top += 420;
        g.DrawString("2  Gradients - smooth steps, no banding", body, Brushes.Black, 150, top);
        var ramps = new[] { Color.Black, Color.FromArgb(0, 174, 239), Color.FromArgb(236, 0, 140), Color.FromArgb(255, 242, 0) };
        for (int r = 0; r < ramps.Length; r++)
        {
            var rect = new Rectangle(150, top + 60 + r * 85, W - 300, 70);
            using var lg = new LinearGradientBrush(rect, Color.White, color || r == 0 ? ramps[r] : Grey(ramps[r]), 0f);
            g.FillRectangle(lg, rect);
        }

        top += 440;
        g.DrawString("3  Nozzle grid - all lines must be continuous", body, Brushes.Black, 150, top);
        using var thin = new Pen(Color.Black, 1);
        for (int x = 150; x <= W - 150; x += 18) g.DrawLine(thin, x, top + 60, x, top + 360);
        using var thinH = new Pen(Color.Black, 1);
        for (int y = top + 60; y <= top + 360; y += 18) g.DrawLine(thinH, 150, y, W - 150, y);

        top += 440;
        g.DrawString("4  Text sharpness", body, Brushes.Black, 150, top);
        int ty = top + 60;
        foreach (var size in new[] { 18, 24, 32, 42 })
        {
            using var f = new Font("Segoe UI", size, GraphicsUnit.Pixel);
            g.DrawString($"{size / 4.17f:0.#} pt  The quick brown fox jumps over the lazy dog 0123456789", f, Brushes.Black, 150, ty);
            ty += size + 22;
        }

        top = ty + 40;
        g.DrawString("5  Greyscale 0-100 %", body, Brushes.Black, 150, top);
        int gw = (W - 300) / 11;
        for (int i = 0; i <= 10; i++)
        {
            int v = 255 - i * 25;
            using var br = new SolidBrush(Color.FromArgb(v, v, v));
            g.FillRectangle(br, 150 + i * gw, top + 60, gw - 6, 120);
            g.DrawString($"{i * 10}", small, Brushes.Black, 150 + i * gw, top + 190);
        }
        using var border = new Pen(Color.Black, 2);
        g.DrawRectangle(border, 100, 100, W - 200, H - 200);
        return bmp;
    }

    static Color Grey(Color c) { int v = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114); return Color.FromArgb(v, v, v); }
}
