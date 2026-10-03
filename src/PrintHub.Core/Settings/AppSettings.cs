using System.Text.Json;
using System.Text.Json.Serialization;
using PrintHub.Core.Discovery;
using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;
using PrintHub.Core.Printing;
using PrintHub.Core.Scanning;

namespace PrintHub.Core.Settings;

/// <summary>A printer the user added by address, remembered between runs (discovery rebuilds everything else).</summary>
public sealed class SavedPrinter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string? Address { get; set; }
    public string? IppUri { get; set; }
    public string? EsclUri { get; set; }
    public string? WebUri { get; set; }

    public static SavedPrinter From(PrinterDevice d) => new()
    {
        Id = d.Id, Name = d.Name, Manufacturer = d.Manufacturer, Address = d.Address,
        IppUri = d.IppUri?.ToString(), EsclUri = d.EsclUri?.ToString(), WebUri = d.WebUri?.ToString(),
    };

    public PrinterDevice ToDevice() => new()
    {
        Id = Id, Name = Name, Model = Name, Manufacturer = Manufacturer, Address = Address,
        IppUri = Uri.TryCreate(IppUri, UriKind.Absolute, out var i) ? i : null,
        EsclUri = Uri.TryCreate(EsclUri, UriKind.Absolute, out var e) ? e : null,
        WebUri = Uri.TryCreate(WebUri, UriKind.Absolute, out var w) ? w : null,
    };
}

public enum ShortcutKind { Scan, Copy }

/// <summary>One-tap workflow (HP Smart's "Smart Tasks"): scan with fixed settings and save, print or open the result.</summary>
public sealed class ShortcutDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New shortcut";
    public string Glyph { get; set; } = "";
    public ShortcutKind Kind { get; set; } = ShortcutKind.Scan;

    public ScanSource Source { get; set; } = ScanSource.Flatbed;
    public ScanColor Color { get; set; } = ScanColor.Color;
    public int Dpi { get; set; } = 300;
    public string PaperName { get; set; } = PaperSize.Auto.Name;
    public OutputFormat Format { get; set; } = OutputFormat.Pdf;
    public bool AutoCrop { get; set; } = true;
    public bool Enhance { get; set; }
    public bool Searchable { get; set; }
    public string? Folder { get; set; }
    public bool OpenAfter { get; set; } = true;
    /// <summary>Also print this many copies of the result (0 = don't).</summary>
    public int PrintCopies { get; set; }
    public int CopyCount { get; set; } = 1;
    public bool IdCard { get; set; }

    public ScanSettings ToScanSettings() => new()
    {
        Source = Source, Color = Color, Dpi = Dpi, Format = Format, AutoCrop = AutoCrop, Enhance = Enhance, Ocr = Searchable,
        Paper = PaperSize.All.FirstOrDefault(p => p.Name == PaperName) ?? PaperSize.Auto, MultiPage = false,
    };

    public static List<ShortcutDef> Defaults() => new()
    {
        new() { Name = "Scan to PDF", Glyph = "", Format = OutputFormat.Pdf, Enhance = true, Searchable = true },
        new() { Name = "Scan receipt", Glyph = "", Color = ScanColor.BlackAndWhite, Dpi = 300, Format = OutputFormat.Pdf, AutoCrop = true },
        new() { Name = "Scan photo", Glyph = "", Dpi = 600, Format = OutputFormat.Jpeg },
        new() { Name = "Scan ID card", Glyph = "", Dpi = 600, PaperName = PaperSize.IdCard.Name, Format = OutputFormat.Jpeg, AutoCrop = false },
        new() { Name = "Quick copy (black & white)", Glyph = "", Kind = ShortcutKind.Copy, Color = ScanColor.Grayscale, Dpi = 300 },
        new() { Name = "Copy ID card", Glyph = "", Kind = ShortcutKind.Copy, IdCard = true },
    };
}

public sealed class AppSettings
{
    public string? SaveFolder { get; set; }
    public string? SelectedPrinterId { get; set; }
    /// <summary>When a printer is reachable by network and USB, use USB.</summary>
    public bool PreferUsb { get; set; }
    public PrintRoute PrintRoute { get; set; } = PrintRoute.Auto;
    public bool OpenFolderAfterSave { get; set; }
    public bool SearchablePdf { get; set; } = true;
    public string Theme { get; set; } = "System";
    public List<SavedPrinter> Printers { get; set; } = new();
    public List<ShortcutDef> Shortcuts { get; set; } = ShortcutDef.Defaults();

    [JsonIgnore] public string EffectiveSaveFolder => string.IsNullOrWhiteSpace(SaveFolder) ? ExportService.DefaultFolder() : SaveFolder;
}

public static class SettingsStore
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath { get; } = Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
            }
            catch { /* corrupt file: start fresh, the old one is overwritten on next save */ }
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
