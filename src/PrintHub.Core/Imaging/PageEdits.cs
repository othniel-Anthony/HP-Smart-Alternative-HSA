namespace PrintHub.Core.Imaging;

public enum PageFilter { Original, Enhance, Grayscale, BlackAndWhite }

public sealed record TextAnnotation(string Text, float X, float Y, float SizeFraction, string ColorHex = "#000000", bool Bold = false);

/// <summary>Placed image such as a signature. X/Y are the top-left corner, WidthFraction is relative to page width.</summary>
public sealed record ImageAnnotation(byte[] Png, float X, float Y, float WidthFraction);

/// <summary>Non-destructive edit stack applied to the original scan each time it is rendered.</summary>
public sealed class PageEdits
{
    /// <summary>Fine rotation in degrees, positive = clockwise (deskew).</summary>
    public double Straighten { get; set; }
    /// <summary>0, 90, 180 or 270 clockwise.</summary>
    public int Rotation { get; set; }
    /// <summary>Crop rectangle as fractions of the rotated image (x, y, w, h); null = no crop.</summary>
    public (float X, float Y, float W, float H)? Crop { get; set; }
    /// <summary>Quarter turn (0/90/180/270) applied after cropping. Used only by export to stand a sideways page upright.</summary>
    public int ExtraTurn { get; set; }
    public PageFilter Filter { get; set; }
    /// <summary>-100..100</summary>
    public int Brightness { get; set; }
    /// <summary>-100..100</summary>
    public int Contrast { get; set; }
    public List<TextAnnotation> Texts { get; } = new();
    public List<ImageAnnotation> Images { get; } = new();

    public bool IsIdentity => Straighten == 0 && Rotation == 0 && Crop is null && Filter == PageFilter.Original
                              && Brightness == 0 && Contrast == 0 && Texts.Count == 0 && Images.Count == 0;

    public void RotateRight() => Rotation = (Rotation + 90) % 360;
    public void RotateLeft() => Rotation = (Rotation + 270) % 360;

    public PageEdits Clone()
    {
        var c = new PageEdits { Straighten = Straighten, Rotation = Rotation, Crop = Crop, ExtraTurn = ExtraTurn, Filter = Filter, Brightness = Brightness, Contrast = Contrast };
        c.Texts.AddRange(Texts); c.Images.AddRange(Images);
        return c;
    }
}

/// <summary>A scanned/imported page: original bytes plus its edit stack.</summary>
public sealed class ScannedPage
{
    public Guid Id { get; } = Guid.NewGuid();
    public byte[] Original { get; set; }
    public PageEdits Edits { get; set; } = new();
    /// <summary>Resolution the page was captured at, used to size PDF pages.</summary>
    public int Dpi { get; set; } = 300;
    public string? SourceName { get; set; }

    public ScannedPage(byte[] original, int dpi = 300) { Original = original; Dpi = dpi; }
}
