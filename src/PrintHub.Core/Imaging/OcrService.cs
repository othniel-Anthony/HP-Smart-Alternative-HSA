using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PrintHub.Core.Imaging;

public sealed record OcrWord(string Text, double X, double Y, double W, double H);
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

public sealed class OcrResult
{
    public string Text { get; init; } = "";
    public IReadOnlyList<OcrLine> Lines { get; init; } = Array.Empty<OcrLine>();
    public int Width { get; init; }
    public int Height { get; init; }
    public string Language { get; init; } = "";
}

/// <summary>Text recognition using the Windows built-in OCR engine (installed language packs only, nothing leaves the PC).</summary>
public static class OcrService
{
    public static bool IsAvailable => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    public static IReadOnlyList<string> Languages => OcrEngine.AvailableRecognizerLanguages.Select(l => l.DisplayName).ToList();

    static readonly System.Text.RegularExpressions.Regex WordLike =
        new(@"^(?:[A-Za-z][a-z]{2,}|[A-Z]{3,})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>(plausible words, all tokens). A plausible word is plain letters, normally cased, with a vowel - gibberish from
    /// text read upside down ("pueMJ0J", "61-l!lun") fails this, so it separates upright text from sideways noise.</summary>
    public static (int Good, int Total) WordStats(OcrResult r)
    {
        int good = 0, total = 0;
        foreach (var w in r.Lines.SelectMany(l => l.Words))
        {
            total++;
            if (WordLike.IsMatch(w.Text) && w.Text.Any(c => "aeiouyAEIOUY".Contains(c))) good++;
        }
        return (good, total);
    }

    /// <summary>
    /// Reads a page that may have been scanned sideways or upside down. Tries upright first and accepts it when most of what it
    /// found looks like real words; otherwise compares all four orientations. <c>Rotation</c> is the clockwise turn that made
    /// the text readable.
    /// </summary>
    public static async Task<(OcrResult Result, int Rotation)> RecognizeUprightAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        var best = await RecognizeAsync(imageBytes, ct);
        var (g0, t0) = WordStats(best);
        if (g0 >= 5 && g0 >= t0 * 0.75) return (best, 0);

        int bestGood = g0, bestRotation = 0;
        using var bmp = ImageTools.Load(imageBytes);
        foreach (var degrees in new[] { 90, 270, 180 })
        {
            using var turned = ImageTools.RotateQuarter(bmp, degrees);
            var res = await RecognizeAsync(ImageTools.Encode(turned, OutputFormat.Png), ct);
            var (good, _) = WordStats(res);
            if (good > bestGood) { best = res; bestGood = good; bestRotation = degrees; }
        }
        return (best, bestRotation);
    }
    public static async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR language pack is installed. Add one in Windows Settings > Time & language > Language & region.");

        // Windows OCR rejects very large images; downscale when needed and map the word boxes back afterwards.
        int w, h;
        double scale = 1;
        using (var probe = ImageTools.Load(imageBytes))
        {
            w = probe.Width; h = probe.Height;
            int max = (int)OcrEngine.MaxImageDimension;
            if (Math.Max(w, h) > max)
            {
                scale = (double)max / Math.Max(w, h);
                using var small = ImageTools.Resize(probe, max);
                imageBytes = ImageTools.Encode(small, OutputFormat.Png);
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageBytes.AsBuffer()).AsTask(ct);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);

        var lines = result.Lines.Select(l => new OcrLine(l.Text,
            l.Words.Select(wd => new OcrWord(wd.Text, wd.BoundingRect.X / scale, wd.BoundingRect.Y / scale, wd.BoundingRect.Width / scale, wd.BoundingRect.Height / scale)).ToList())).ToList();

        return new OcrResult { Text = result.Text, Lines = lines, Width = w, Height = h, Language = engine.RecognizerLanguage.DisplayName };
    }
}
