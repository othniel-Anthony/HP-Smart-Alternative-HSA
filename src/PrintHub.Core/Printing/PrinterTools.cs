using System.Text;
using PrintHub.Core.Discovery;
using PrintHub.Core.Imaging;
using PrintHub.Core.Ipp;
using PrintHub.Core.Usb;

namespace PrintHub.Core.Printing;

/// <summary>Maintenance helpers: diagnostic page, identify, and a copyable support report.</summary>
public static class PrinterTools
{
    public static async Task PrintTestPageAsync(PrinterDevice dev, PrinterSession? session, PrintRoute route = PrintRoute.Auto, bool color = true, CancellationToken ct = default)
    {
        using var page = TestPage.Create(dev.Name, session?.ViaUsb == true ? "USB" : dev.Connection, color);
        var src = PrintSource.FromBitmaps(new[] { page }, TestPage.Dpi, "HSA test page");
        await PrintService.PrintAsync(dev, session, src, new PrintOptions { Route = route, Color = color, Borderless = true, Quality = PrintQuality.Best }, ct);
    }

    public const string BundledTestPageName = "print-color-test-page-basic-1.pdf";

    /// <summary>The colour test page that ships inside the app, written to a temp file (the original bytes, untouched).</summary>
    public static string ExtractBundledTestPage()
    {
        using var res = typeof(PrinterTools).Assembly.GetManifestResourceStream(BundledTestPageName)
            ?? throw new InvalidOperationException("The bundled test page is missing from this build.");
        var dir = Path.Combine(Path.GetTempPath(), "HSA");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, BundledTestPageName);
        if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
        {
            using var fs = File.Create(path);
            res.CopyTo(fs);
        }
        return path;
    }

    /// <summary>Print the bundled colour test page exactly as it is: colour, fitted to the page, no other changes.</summary>
    public static async Task PrintBundledTestPageAsync(PrinterDevice dev, PrinterSession? session, PrintRoute route = PrintRoute.Auto, CancellationToken ct = default)
    {
        var file = ExtractBundledTestPage();
        await PrintService.PrintFileAsync(dev, session, file, new PrintOptions { Route = route, Color = true, Scale = ScaleMode.FitToPage, Quality = PrintQuality.Normal }, ct);
    }

    public static async Task IdentifyAsync(PrinterSession session, CancellationToken ct = default)
    {
        if (session.Ipp is null) throw new InvalidOperationException("Identify needs an IPP connection to the printer.");
        await session.Ipp.IdentifyAsync(ct);
    }

    /// <summary>Plain-text report to paste into a support request. Contains no document data.</summary>
    public static async Task<string> BuildReportAsync(PrinterDevice dev, PrinterSession? session, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HP Smart Alternative (HSA) {typeof(PrinterTools).Assembly.GetName().Version?.ToString(3)} - printer report {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine($"Name:          {dev.Name}");
        sb.AppendLine($"Manufacturer:  {dev.Manufacturer}");
        sb.AppendLine($"Connection:    {dev.Connection}{(session?.ViaUsb == true ? " (session over USB)" : "")}");
        sb.AppendLine($"Address:       {dev.Address}");
        sb.AppendLine($"IPP:           {dev.IppUri}");
        sb.AppendLine($"eSCL:          {dev.EsclUri}");
        sb.AppendLine($"Web (EWS):     {session?.WebUri ?? dev.WebUri}");
        sb.AppendLine($"Windows queue: {dev.SpoolerName}  port={dev.SpoolerPort}  driver={dev.SpoolerDriver}");
        sb.AppendLine($"WIA id:        {dev.WiaDeviceId}");
        foreach (var u in dev.UsbCandidates) sb.AppendLine($"USB:           {u.Describe()}");
        try
        {
            // every interface Windows reports for this printer, with the driver each one is bound to: shows why a USB web page or ink levels are unavailable
            foreach (var u in UsbDeviceScanner.Scan(presentOnly: true)
                         .Where(u => dev.UsbCandidates.Any(c => c.VendorId == u.VendorId && c.ProductId == u.ProductId) || PrinterDevice.Similar(u.Name, dev.Name))
                         .Where(u => !dev.UsbCandidates.Any(c => c.InstanceId == u.InstanceId)))
                sb.AppendLine($"USB (other):   {u.Describe()}");
        }
        catch (Exception ex) { sb.AppendLine($"USB scan failed: {ex.Message}"); }

        if (session?.Ipp is { } ipp)
        {
            try
            {
                var st = await ipp.GetStatusAsync(ct);
                sb.AppendLine().AppendLine("IPP status");
                sb.AppendLine($"  Model:       {st.MakeAndModel}");
                sb.AppendLine($"  Firmware:    {st.Firmware}");
                sb.AppendLine($"  Serial:      {st.SerialNumber}");
                sb.AppendLine($"  State:       {st.State} {st.StateMessage}");
                sb.AppendLine($"  Alerts:      {string.Join("; ", st.Alerts)}");
                sb.AppendLine($"  Formats:     {string.Join(", ", st.DocumentFormats)}");
                foreach (var s in st.Supplies) sb.AppendLine($"  Supply:      {s.Name} ({s.Type}) {(s.Percent < 0 ? "unknown" : s.Percent + "%")}");
            }
            catch (Exception ex) { sb.AppendLine().AppendLine($"IPP status failed: {ex.Message}"); }
        }
        if (session?.Escl is { } escl)
        {
            try
            {
                var c = await escl.GetCapabilitiesAsync(ct);
                sb.AppendLine().AppendLine($"eSCL {c.Version}: flatbed={(c.Flatbed is null ? "no" : "yes")}, feeder={(c.Feeder is null ? "no" : "yes")}, duplex={c.FeederDuplex}");
                if (c.Flatbed is not null) sb.AppendLine($"  Flatbed {c.Flatbed.MaxWidth / 300.0:0.##} x {c.Flatbed.MaxHeight / 300.0:0.##} in, dpi {string.Join("/", c.Flatbed.Resolutions)}");
            }
            catch (Exception ex) { sb.AppendLine().AppendLine($"eSCL query failed: {ex.Message}"); }
        }
        sb.AppendLine().AppendLine($"OCR available: {OcrService.IsAvailable}");

        // the last few USB / scanner-driver / print-timing log lines from today, so a support request carries the evidence
        try
        {
            var log = Path.Combine(Settings.AppPaths.DataDir, "logs", $"printhub-{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(log))
            {
                var recent = File.ReadLines(log).Where(l => l.Contains("USB") || l.Contains("WIA") || l.Contains("Windows print") || l.Contains("eSCL") || l.Contains("HP web services")).TakeLast(40).ToList();
                if (recent.Count > 0) { sb.AppendLine().AppendLine("Recent log lines"); foreach (var l in recent) sb.AppendLine("  " + l); }
            }
        }
        catch { }
        return sb.ToString();
    }
}
