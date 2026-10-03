using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;

namespace PrintHub.Core.Scanning;

public sealed record PaperSize(string Name, double WidthIn, double HeightIn)
{
    public bool IsAuto => WidthIn <= 0;
    public override string ToString() => Name;

    public static readonly PaperSize Auto = new("Auto (full bed)", 0, 0);
    public static readonly PaperSize Letter = new("Letter 8.5 × 11 in", 8.5, 11);
    public static readonly PaperSize A4 = new("A4 210 × 297 mm", 8.27, 11.69);
    public static readonly PaperSize Legal = new("Legal 8.5 × 14 in", 8.5, 14);
    public static readonly PaperSize A5 = new("A5 148 × 210 mm", 5.83, 8.27);
    public static readonly PaperSize Photo4x6 = new("Photo 4 × 6 in", 4, 6);
    public static readonly PaperSize Photo5x7 = new("Photo 5 × 7 in", 5, 7);
    public static readonly PaperSize IdCard = new("ID card 3.37 × 2.13 in", 3.37, 2.13);
    public static readonly PaperSize BusinessCard = new("Business card 3.5 × 2 in", 3.5, 2);
    public static readonly PaperSize[] All = { Auto, Letter, A4, Legal, A5, Photo4x6, Photo5x7, IdCard, BusinessCard };
}

public sealed class ScanSettings
{
    public ScanSource Source { get; set; } = ScanSource.Flatbed;
    public ScanColor Color { get; set; } = ScanColor.Color;
    public int Dpi { get; set; } = 300;
    public PaperSize Paper { get; set; } = PaperSize.Auto;
    public OutputFormat Format { get; set; } = OutputFormat.Pdf;
    /// <summary>Detect the document on the glass and crop to it (flatbed with Auto size).</summary>
    public bool AutoCrop { get; set; } = true;
    public bool AutoStraighten { get; set; }
    public bool Enhance { get; set; }
    /// <summary>Keep prompting for more pages after each flatbed scan.</summary>
    public bool MultiPage { get; set; } = true;
    public bool Ocr { get; set; }

    public ScanSettings Clone() => (ScanSettings)MemberwiseClone();
}

/// <summary>Named settings bundle, like HP Smart's "Document", "Photo" and "ID card" scan modes.</summary>
public sealed record ScanPreset(string Name, string Description, string Glyph, ScanSettings Settings)
{
    public static IReadOnlyList<ScanPreset> Defaults { get; } = new[]
    {
        new ScanPreset("Document", "Color, 300 dpi, saved as PDF", "", new ScanSettings { Dpi = 300, Format = OutputFormat.Pdf, AutoCrop = true, Enhance = true }),
        new ScanPreset("Black & white document", "Sharp text, small files", "", new ScanSettings { Color = ScanColor.BlackAndWhite, Dpi = 300, Format = OutputFormat.Pdf, AutoCrop = true }),
        new ScanPreset("Photo", "High quality JPEG, 600 dpi", "", new ScanSettings { Dpi = 600, Format = OutputFormat.Jpeg, AutoCrop = true, MultiPage = false }),
        new ScanPreset("ID card", "Small area, 600 dpi JPEG", "", new ScanSettings { Dpi = 600, Paper = PaperSize.IdCard, Format = OutputFormat.Jpeg, AutoCrop = true, MultiPage = false }),
        new ScanPreset("Book", "Page by page, straightened", "", new ScanSettings { Dpi = 300, Format = OutputFormat.Pdf, AutoCrop = true, AutoStraighten = true, Enhance = true }),
        new ScanPreset("Document feeder", "Whole stack from the ADF as one PDF", "", new ScanSettings { Source = ScanSource.Feeder, Dpi = 300, Format = OutputFormat.Pdf, MultiPage = false, AutoCrop = false }),
    };
}
