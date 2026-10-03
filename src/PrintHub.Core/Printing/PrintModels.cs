namespace PrintHub.Core.Printing;

public enum DuplexMode { Off, LongEdge, ShortEdge }
public enum ScaleMode { FitToPage, FillPage, ActualSize, PhotoSize }
public enum PrintRoute { Auto, WindowsDriver, DirectIpp }
public enum PrintQuality { Draft, Normal, Best }

public sealed record PaperChoice(string Name, double WidthIn, double HeightIn, string IppMedia, params string[] WindowsNames)
{
    public override string ToString() => Name;

    public static readonly PaperChoice Default = new("Printer default", 0, 0, "", Array.Empty<string>());
    public static readonly PaperChoice Letter = new("Letter 8.5 × 11 in", 8.5, 11, "na_letter_8.5x11in", "Letter");
    public static readonly PaperChoice A4 = new("A4", 8.27, 11.69, "iso_a4_210x297mm", "A4");
    public static readonly PaperChoice Legal = new("Legal 8.5 × 14 in", 8.5, 14, "na_legal_8.5x14in", "Legal");
    public static readonly PaperChoice A5 = new("A5", 5.83, 8.27, "iso_a5_148x210mm", "A5");
    public static readonly PaperChoice Photo4x6 = new("Photo 4 × 6 in", 4, 6, "na_index-4x6_4x6in", "4 x 6", "4x6", "Photo 4x6", "4 x 6 in", "10 x 15 cm", "100 x 150 mm", "4\"x6\"");
    public static readonly PaperChoice Photo5x7 = new("Photo 5 × 7 in", 5, 7, "na_5x7_5x7in", "5 x 7", "5x7", "5 x 7 in", "13 x 18 cm", "5\"x7\"");
    public static readonly PaperChoice[] All = { Default, Letter, A4, Legal, A5, Photo4x6, Photo5x7 };
}

public sealed class PrintOptions
{
    public int Copies { get; set; } = 1;
    public bool Collate { get; set; } = true;
    public bool Color { get; set; } = true;
    public DuplexMode Duplex { get; set; } = DuplexMode.Off;
    public ScaleMode Scale { get; set; } = ScaleMode.FitToPage;
    public PaperChoice Paper { get; set; } = PaperChoice.Default;
    public PrintQuality Quality { get; set; } = PrintQuality.Normal;
    /// <summary>Print area for <see cref="ScaleMode.PhotoSize"/>, in inches.</summary>
    public double PhotoWidthIn { get; set; } = 4;
    public double PhotoHeightIn { get; set; } = 6;
    /// <summary>"1-3,5" style page selection; null/empty = all.</summary>
    public string? PageRange { get; set; }
    /// <summary>No white margin around the image.</summary>
    public bool Borderless { get; set; }
    public PrintRoute Route { get; set; } = PrintRoute.Auto;
    /// <summary>Windows route only: write the output to this file instead of paper (works with "Microsoft Print to PDF").</summary>
    public string? PrintToFilePath { get; set; }

    public PrintOptions Clone() => (PrintOptions)MemberwiseClone();

    /// <summary>Parses a page range like "1-3, 5" against a page count. Returns zero-based indexes in order.</summary>
    public static List<int> ParseRange(string? text, int pageCount)
    {
        var all = Enumerable.Range(0, pageCount).ToList();
        if (string.IsNullOrWhiteSpace(text)) return all;
        var result = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(bits[0], out var lo)) throw new FormatException($"'{part}' is not a valid page range.");
            int hi = lo;
            if (bits.Length == 2 && !int.TryParse(bits[1], out hi)) throw new FormatException($"'{part}' is not a valid page range.");
            if (hi < lo) (lo, hi) = (hi, lo);
            for (int p = lo; p <= hi; p++) if (p >= 1 && p <= pageCount) result.Add(p - 1);
        }
        if (result.Count == 0) throw new FormatException($"The page range '{text}' selects no pages (document has {pageCount}).");
        return result;
    }
}
