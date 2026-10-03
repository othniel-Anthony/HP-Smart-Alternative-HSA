using PrintHub.Core.Discovery;
using PrintHub.Core.Escl;
using PrintHub.Core.Printing;
using PrintHub.Core.Scanning;

namespace PrintHub.Core.Settings;

public static class ShortcutRunner
{
    /// <summary>Runs a shortcut. Returns the files it created (empty for copy shortcuts).</summary>
    public static async Task<List<string>> RunAsync(ShortcutDef s, PrinterDevice dev, PrinterSession? session, AppSettings settings,
        Func<Task>? promptNextSide = null, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (s.Kind == ShortcutKind.Copy)
        {
            await CopyService.CopyAsync(dev, session, new CopySettings
            {
                Copies = Math.Max(1, s.CopyCount), Color = s.Color == ScanColor.Color, Source = s.Source, IdCard = s.IdCard,
                Quality = s.Dpi >= 600 ? PrintQuality.Best : s.Dpi <= 150 ? PrintQuality.Draft : PrintQuality.Normal,
                Route = settings.PrintRoute,
            }, promptNextSide, status, ct);
            return new();
        }

        var scan = s.ToScanSettings();
        var pages = new List<Imaging.ScannedPage>();
        status?.Report("Scanning…");
        await foreach (var p in ScanService.ScanAsync(session, dev, scan, ct))
        {
            pages.Add(p);
            status?.Report($"Scanned {pages.Count} page{(pages.Count == 1 ? "" : "s")}");
        }

        status?.Report("Saving…");
        var folder = string.IsNullOrWhiteSpace(s.Folder) ? settings.EffectiveSaveFolder : s.Folder;
        var files = await ExportService.SaveAsync(pages, s.Format, folder, ExportService.SuggestName(s.Name), s.Searchable, null, ct);

        if (s.PrintCopies > 0)
        {
            status?.Report("Printing…");
            var src = PrintSource.FromBitmaps(ExportService.RenderAll(pages), scan.Dpi, s.Name);
            await PrintService.PrintAsync(dev, session, src, new PrintOptions { Copies = s.PrintCopies, Route = settings.PrintRoute }, ct);
        }
        status?.Report($"Done: {Path.GetFileName(files[0])}");
        return files;
    }
}
