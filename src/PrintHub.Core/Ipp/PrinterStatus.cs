namespace PrintHub.Core.Ipp;

public enum PrinterState { Unknown, Idle, Processing, Stopped }

public sealed record SupplyLevel(string Name, string Type, string ColorHex, int Level, int Low, int High)
{
    /// <summary>Level 0-100, or -1 when the printer reports unknown (-1), not-reported (-2) or "some remaining" (-3).</summary>
    public int Percent => Level is < 0 ? -1 : High > 0 && High != 100 ? Math.Clamp(Level * 100 / High, 0, 100) : Math.Clamp(Level, 0, 100);
    public bool IsLow => Percent >= 0 && Percent <= Math.Max(Low, 10);
    public bool IsWaste => Type.Contains("waste", StringComparison.OrdinalIgnoreCase);
}

public sealed class PrinterStatus
{
    public string MakeAndModel { get; init; } = "";
    public string Info { get; init; } = "";
    public string Location { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string Firmware { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public PrinterState State { get; init; }
    public string StateMessage { get; init; } = "";
    public List<string> StateReasons { get; init; } = new();
    public List<SupplyLevel> Supplies { get; init; } = new();
    public List<string> DocumentFormats { get; init; } = new();
    public List<string> MediaSupported { get; init; } = new();
    public List<string> MediaReady { get; init; } = new();
    public List<string> SidesSupported { get; init; } = new();
    public List<string> ColorModes { get; init; } = new();
    public List<string> OperationsSupported { get; init; } = new();
    public int? PagesPerMinute { get; init; }
    public bool ColorSupported { get; init; }
    public string? MoreInfoUri { get; init; }
    public Dictionary<string, string> DeviceId { get; init; } = new();

    public bool SupportsDuplex => SidesSupported.Any(s => s.StartsWith("two-sided"));
    public bool SupportsPdf => DocumentFormats.Contains("application/pdf");
    public bool SupportsJpeg => DocumentFormats.Contains("image/jpeg");

    /// <summary>Human readable alerts, excluding the noise reasons ("none", "*-report").</summary>
    public IEnumerable<string> Alerts => StateReasons
        .Where(r => r != "none" && !r.EndsWith("-report"))
        .Select(Humanize);

    public static string Humanize(string reason)
    {
        var sev = reason.EndsWith("-error") ? "Error: " : reason.EndsWith("-warning") ? "Warning: " : "";
        var core = reason.Replace("-error", "").Replace("-warning", "").Replace("-report", "").Replace('-', ' ');
        return sev + char.ToUpper(core[0]) + core[1..];
    }

    public static PrinterStatus FromMessage(IppMessage m)
    {
        var levels = m.Get("marker-levels")?.Values.OfType<int>().ToList() ?? new();
        var names = m.GetStrings("marker-names");
        var types = m.GetStrings("marker-types");
        var colors = m.GetStrings("marker-colors");
        var lows = m.Get("marker-low-levels")?.Values.OfType<int>().ToList() ?? new();
        var highs = m.Get("marker-high-levels")?.Values.OfType<int>().ToList() ?? new();

        var supplies = new List<SupplyLevel>();
        for (int i = 0; i < levels.Count; i++)
            supplies.Add(new SupplyLevel(
                i < names.Count ? names[i] : $"Supply {i + 1}",
                i < types.Count ? types[i] : "",
                i < colors.Count ? colors[i] : "#808080",
                levels[i],
                i < lows.Count ? lows[i] : 10,
                i < highs.Count ? highs[i] : 100));

        var state = m.GetInt("printer-state") switch { 3 => PrinterState.Idle, 4 => PrinterState.Processing, 5 => PrinterState.Stopped, _ => PrinterState.Unknown };

        var devId = new Dictionary<string, string>();
        foreach (var part in (m.GetString("printer-device-id") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = part.IndexOf(':');
            if (c > 0) devId[part[..c].Trim().ToUpperInvariant()] = part[(c + 1)..].Trim();
        }

        return new PrinterStatus
        {
            MakeAndModel = m.GetString("printer-make-and-model") ?? "",
            Info = m.GetString("printer-info") ?? "",
            Location = m.GetString("printer-location") ?? "",
            Uuid = m.GetString("printer-uuid") ?? "",
            Firmware = m.GetString("printer-firmware-string-version") ?? "",
            SerialNumber = devId.GetValueOrDefault("SN") ?? devId.GetValueOrDefault("SERIALNUMBER") ?? "",
            State = state,
            StateMessage = m.GetString("printer-state-message") ?? "",
            StateReasons = m.GetStrings("printer-state-reasons"),
            Supplies = supplies,
            DocumentFormats = m.GetStrings("document-format-supported"),
            MediaSupported = m.GetStrings("media-supported"),
            MediaReady = m.GetStrings("media-ready"),
            SidesSupported = m.GetStrings("sides-supported"),
            ColorModes = m.GetStrings("print-color-mode-supported"),
            OperationsSupported = m.Get("operations-supported")?.Values.OfType<int>().Select(o => $"0x{o:X4}").ToList() ?? new(),
            PagesPerMinute = m.GetInt("pages-per-minute"),
            ColorSupported = m.GetBool("color-supported") ?? false,
            MoreInfoUri = m.GetString("printer-more-info"),
            DeviceId = devId,
        };
    }
}
