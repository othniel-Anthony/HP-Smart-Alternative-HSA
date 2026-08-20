using System.IO;
using System.Text;
using HSA.Models;

namespace HSA.Services;

public sealed class ModelImageService : IModelImageService
{
    private const string PrintersResourceDir = "Resources/printers";

    // Resolution order: more specific first. All keywords must be present (case-insensitive)
    // somewhere in the printer's name / driver / model string.
    private static readonly (string[] Keywords, string ImageFile, string Family)[] Catalog = new[]
    {
        (new[] { "color", "laserjet" }, "laserjet-color.png", "LaserJet (color)"),
        (new[] { "laserjet", "enterprise" }, "laserjet-mono.png", "LaserJet Enterprise"),
        (new[] { "laserjet", "pro" }, "laserjet-mono.png", "LaserJet Pro"),
        (new[] { "laserjet", "mfp" }, "laserjet-color.png", "LaserJet MFP"),
        (new[] { "laserjet" }, "laserjet-mono.png", "LaserJet"),
        (new[] { "officejet" }, "officejet.png", "OfficeJet"),
        (new[] { "envy" }, "envy.png", "ENVY"),
        (new[] { "smart", "tank" }, "smart-tank.png", "Smart Tank"),
        (new[] { "neverstop" }, "smart-tank.png", "Neverstop Laser"),
        (new[] { "pagewide" }, "officejet.png", "PageWide"),
        (new[] { "deskjet" }, "officejet.png", "DeskJet"),
        (new[] { "designjet" }, "officejet.png", "DesignJet")
    };

    private static readonly string[] GenericFallback = new[] { "generic.png" };

    public Uri? GetImageUri(PrinterInfo printer)
    {
        if (printer is null) return null;

        // 1) Exact normalized model name
        var normalized = NormalizeModelName(JoinNameFields(printer));
        if (!string.IsNullOrEmpty(normalized) && FileExists(normalized + ".png"))
        {
            return PackUri(normalized + ".png");
        }

        // 2) Family keyword match
        var haystack = JoinNameFields(printer).ToLowerInvariant();
        foreach (var entry in Catalog)
        {
            if (entry.Keywords.All(k => haystack.Contains(k)))
                return PackUri(entry.ImageFile);
        }

        // 3) Generic
        return PackUri(GenericFallback[0]);
    }

    public string? GetFamily(PrinterInfo printer)
    {
        if (printer is null) return null;
        var haystack = JoinNameFields(printer).ToLowerInvariant();
        foreach (var entry in Catalog)
        {
            if (entry.Keywords.All(k => haystack.Contains(k)))
                return entry.Family;
        }
        return "HP printer";
    }

    private static string JoinNameFields(PrinterInfo p)
    {
        // Concatenate the fields that identify the model so the keyword match has the
        // most signal. Driver name often includes the family even when the friendly
        // printer name doesn't (e.g. a renamed share).
        var parts = new[] { p.Model, p.DriverName, p.Name };
        return string.Join(' ', parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string NormalizeModelName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lower = s.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_' || c == '/') sb.Append('-');
        }
        var result = sb.ToString();
        while (result.Contains("--")) result = result.Replace("--", "-");
        return result.Trim('-');
    }

    private static bool FileExists(string fileName)
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, "Resources", "printers", fileName);
        if (File.Exists(localPath)) return true;
        // dev-time check: the build process sometimes leaves files in the project root
        var devPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "printers", fileName);
        return File.Exists(Path.GetFullPath(devPath));
    }

    private static Uri PackUri(string fileName)
    {
        // WPF pack URI: assembly-relative resource
        return new Uri($"pack://application:,,,/{PrintersResourceDir}/{fileName}");
    }
}
