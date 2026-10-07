using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PrintHub.Core.Discovery;
using PrintHub.Core.Ipp;

namespace PrintHub.Core.Printing;

public enum BrotherCleanScope { All, Black, Color }
public enum BrotherCleanStrength { Normal, Strong, Strongest }

/// <summary>What a Brother inkjet reports about itself in answer to <c>@PJL INFO BRSUPPLY</c>: ink, counters, firmware.</summary>
public sealed class BrotherReport
{
    public string Raw { get; init; } = "";
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);

    public string? Get(string key) => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
    public long? GetNumber(string key) => long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public string? Model => Get("INK_MODEL_NAME");
    public string? Serial => Get("INK_MACHINE_SN");
    /// <summary>"8CH-633-001:Ver.1.38" style: the firmware's own code.</summary>
    public string? Firmware => Get("INK_MODEL_CODE");
    public long? TotalPages => GetNumber("INK_PAGECOUNT_TOTAL");

    /// <summary>Ink left in each cartridge as the printer estimates it, in the order Black, Cyan, Magenta, Yellow. A colour the printer did not report is left out.</summary>
    public List<SupplyLevel> Ink
    {
        get
        {
            var list = new List<SupplyLevel>();
            foreach (var (name, key, hex) in new[] { ("Black", "BLACK", "#202020"), ("Cyan", "CYAN", "#00AEEF"), ("Magenta", "MAGENTA", "#EC008C"), ("Yellow", "YELLOW", "#FFD400") })
            {
                var raw = Get("INK_REMAINING_" + key);
                if (raw is null || !int.TryParse(raw.TrimEnd('%', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct)) continue;
                list.Add(new SupplyLevel(name, "ink", hex, Math.Clamp(pct, 0, 100), 10, 100));
            }
            return list;
        }
    }

    public string? CartridgeName(string colour) => Get("INK_CART_NAME_" + colour.ToUpperInvariant());

    /// <summary>Cleanings the printer counted: started by someone (manual) and by itself (automatic), added over every kind of purge it knows.</summary>
    public long ManualPurges => SumWhere(k => k.StartsWith("INK_PURGECNT_", StringComparison.Ordinal) && k.Contains("_MANU_", StringComparison.Ordinal));
    public long AutoPurges => SumWhere(k => k.StartsWith("INK_PURGECNT_", StringComparison.Ordinal) && k.Contains("_AUTO", StringComparison.Ordinal));
    /// <summary>How many sheets "Check Print Quality" has printed so far (it is how a successful check is told from a missed command).</summary>
    public long? QualityChecksPrinted => GetNumber("INK_PAGECOUNT_PINCHECK");
    /// <summary>Purges whose ink went to the ink absorber, as counted by the printer. Brother does not publish the number at which the absorber counts as full.</summary>
    public long? PurgeWasteCount => GetNumber("INK_PURGEWASTE_COUNT");
    /// <summary>Ink dots the printer has fired for cleaning, all four colours together. It moves while a cleaning runs (the purge total and the check-sheet count are only updated minutes later), so it is what shows that a cleaning happened.</summary>
    public long CleaningDots => new[] { "BLACK", "YELLOW", "MAGENTA", "CYAN" }.Sum(c => GetNumber("INK_DOTCOUNT_CLEANING_" + c) ?? 0);
    public long? WipeCount => GetNumber("INK_WIPEHEAD_COUNT");
    public long? FlushWasteBlack => GetNumber("INK_FLUSHWASTE_COUNT_BK");
    public long? FlushWasteColor => GetNumber("INK_FLUSHWASTE_COUNT_CL");
    public long? AlignmentTries => GetNumber("INK_ALIGNMENT_TRY");
    public long? AlignmentDone => GetNumber("INK_ALIGNMENT_COMPLETE");

    long SumWhere(Func<string, bool> pick) => Values.Where(kv => pick(kv.Key)).Sum(kv => long.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0);

    static readonly Regex Line = new("^(?<k>[A-Za-z0-9_]+)=\"?(?<v>[^\"\\r\\n]*)\"?\\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static BrotherReport Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Line.Matches(text)) values[m.Groups["k"].Value] = m.Groups["v"].Value.Trim();   // a key that appears twice: the later one counts
        return new BrotherReport { Raw = text, Values = values };
    }
}

/// <summary>The printer's own state as <c>@PJL INFO STATUS</c> gives it.</summary>
public sealed record BrotherStatus(string Code, string Display, bool Online)
{
    /// <summary>Ready to take a job: the code PJL uses for "ready", or a resting state the printer wakes from by itself.</summary>
    public bool IsReady => Code == "10001" || Regex.IsMatch(Display, @"^\s*(Ready|Sleep|Deep Sleep|Power Save)", RegexOptions.IgnoreCase);

    public static BrotherStatus? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var code = Regex.Match(text, @"CODE=(\d+)").Groups[1].Value;
        if (code.Length == 0) return null;
        var display = Regex.Match(text, "DISPLAY=\"([^\"]*)\"").Groups[1].Value;
        return new BrotherStatus(code, display, !Regex.IsMatch(text, @"ONLINE=FALSE", RegexOptions.IgnoreCase));
    }
}

/// <summary>Timings of the maintenance jobs. Tests shorten them.</summary>
public sealed record BrotherTimings
{
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>
    /// A printer that never shows as busy is given this long before the job counts as finished. (Measured on an MFC-J5955DW: it stays "Ready" for about
    /// 7 seconds after a cleaning command, then shows "Cleaning" for over a minute.)
    /// </summary>
    public TimeSpan NoBusyGrace { get; init; } = TimeSpan.FromSeconds(25);
    /// <summary>Scales the time a job is given at least / at most (1 = real printer).</summary>
    public double Scale { get; init; } = 1;
    public TimeSpan Answer { get; init; } = TimeSpan.FromSeconds(4);
}

public sealed record BrotherResult(bool Ok, string Summary);

/// <summary>
/// The conversation with a Brother inkjet over its USB print interface. Brother's printers speak PJL there: <c>@PJL INFO ...</c> asks for
/// information, <c>@PJL EXECUTE ...</c> starts a maintenance action. Every command is wrapped in the "universal exit language" escape
/// the way Brother's own Windows driver wraps it.
/// </summary>
public sealed class BrotherPjlChannel : IDisposable
{
    public const string Uel = "\x1b%-12345X";
    readonly Stream _s;
    readonly object _lock = new();
    readonly StringBuilder _rx = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task _pump;

    /// <summary>
    /// One read is always pending, in a loop of its own, and everything the printer sends is collected. (Cancelling a read that is waiting on the USB
    /// port can swallow bytes that arrive at that moment; on a real printer that made an answer go missing now and then.)
    /// </summary>
    public BrotherPjlChannel(Stream s)
    {
        _s = s;
        _pump = Task.Run(async () =>
        {
            var buf = new byte[8192];
            while (!_stop.IsCancellationRequested)
            {
                int n;
                try { n = await _s.ReadAsync(buf, CancellationToken.None).ConfigureAwait(false); }
                catch { break; }   // the port was closed
                if (n > 0) lock (_lock) _rx.Append(Encoding.Latin1.GetString(buf, 0, n));
                else { try { await Task.Delay(20, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }   // an idle USB port answers a read with nothing, at once
            }
        });
    }

    public static byte[] Frame(string command) => Encoding.ASCII.GetBytes(Uel + command + "\r\n" + Uel);

    public async Task SendAsync(string command, CancellationToken ct)
    {
        var tx = Frame(command);
        await _s.WriteAsync(tx, ct).ConfigureAwait(false);
        await _s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Send a query and return what the printer answers between the echo of the command and the form feed that ends the answer, or null when it stays silent.</summary>
    public async Task<string?> AskAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        // the printer sends status lines of its own now and then: what is already here is not the answer
        lock (_lock) _rx.Clear();
        await SendAsync(command, ct).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            string text; lock (_lock) text = _rx.ToString();
            int echo = text.LastIndexOf(command, StringComparison.Ordinal);
            if (echo >= 0)
            {
                int end = text.IndexOf('\f', echo);
                if (end >= 0) return text[(echo + command.Length)..end].Replace("\r", "").Trim('\n', ' ');
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        return null;
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { }
        try { _s.Dispose(); } catch { }   // ends the pending read
        try { _pump.Wait(500); } catch { }
    }
}

/// <summary>
/// Maintenance for Brother inkjets over USB: ink levels and counters, head cleaning (three strengths, three colour groups) and the print quality check
/// sheet. The commands are the ones Brother's own Windows printer driver sends: its Maintenance tab sends <c>@PJL EXECUTE NORMALPURGE=ALL</c> for a
/// cleaning and <c>@PJL EXECUTE PINCHKPRINT</c> for the check sheet (the driver's Strong and Strongest levels are <c>POWERPURGE</c> and
/// <c>SUPERPOWERPURGE</c>, with <c>BLACK</c>, <c>COLOR</c> or <c>ALL</c>); the information queries are the ones its status monitor sends.
/// Brother has no USB command for head alignment or for resetting the ink absorber: both are service-menu functions on the printer itself.
/// </summary>
public static class BrotherMaintenance
{
    public const int BrotherVendor = 0x04F9;
    public const string SupplyQuery = "@PJL INFO BRSUPPLY";
    public const string StatusQuery = "@PJL INFO STATUS";
    public const string QualityCheckCommand = "@PJL EXECUTE PINCHKPRINT";

    public static string CleanCommand(BrotherCleanScope scope, BrotherCleanStrength strength)
    {
        string verb = strength switch { BrotherCleanStrength.Normal => "NORMALPURGE", BrotherCleanStrength.Strong => "POWERPURGE", _ => "SUPERPOWERPURGE" };
        string what = scope switch { BrotherCleanScope.Black => "BLACK", BrotherCleanScope.Color => "COLOR", _ => "ALL" };
        return $"@PJL EXECUTE {verb}={what}";
    }

    public static string Describe(BrotherCleanScope scope, BrotherCleanStrength strength) =>
        $"{strength} cleaning of {scope switch { BrotherCleanScope.Black => "black", BrotherCleanScope.Color => "the colours", _ => "all colours" }}";

    /// <summary>
    /// About how long a cleaning takes: Brother says a minute or two (a normal black cleaning ran for over a minute on an MFC-J5955DW). Only shown to the user and used
    /// to set how long to wait before giving up; the end of the job is told by the printer's own status.
    /// </summary>
    static TimeSpan ExpectedFor(BrotherCleanScope scope, BrotherCleanStrength strength)
    {
        double s = scope == BrotherCleanScope.All ? 130 : 90;
        return TimeSpan.FromSeconds(s * strength switch { BrotherCleanStrength.Normal => 1.0, BrotherCleanStrength.Strong => 1.5, _ => 2.2 });
    }

    /// <summary>The ink the job draws on, per the colour group.</summary>
    public static IEnumerable<string> InksUsed(BrotherCleanScope scope) => scope switch
    {
        BrotherCleanScope.Black => new[] { "Black" },
        BrotherCleanScope.Color => new[] { "Cyan", "Magenta", "Yellow" },
        _ => new[] { "Black", "Cyan", "Magenta", "Yellow" },
    };

    /// <summary>True when this printer's USB print interface is plugged in right now.</summary>
    public static bool IsOnUsb(PrinterDevice dev) => EpsonMaintenance.FindPrintInterface(dev, BrotherVendor) is not null;

    static BrotherPjlChannel Open(PrinterDevice dev, out string path)
    {
        var fs = EpsonMaintenance.OpenUsbStream(dev, out path, BrotherVendor);
        return new BrotherPjlChannel(fs);
    }

    // ---------------------------------------------------------------- public entry points

    public static async Task<BrotherReport> ReadReportAsync(PrinterDevice dev, CancellationToken ct = default)
    {
        using var hold = PrinterActivity.Begin();
        using var ch = Open(dev, out var path);
        Diag.Log($"Brother: reading the report from {path}");
        return await ReadReportAsync(ch, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
    }

    public static async Task<BrotherResult> CleanAsync(PrinterDevice dev, BrotherCleanScope scope, BrotherCleanStrength strength,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var hold = PrinterActivity.Begin();
        using var ch = Open(dev, out var path);
        Diag.Log($"Brother: {Describe(scope, strength)} on '{dev.Name}' via {path}");
        return await CleanAsync(ch, scope, strength, progress, new BrotherTimings(), ct).ConfigureAwait(false);
    }

    public static async Task<BrotherResult> PrintQualityCheckAsync(PrinterDevice dev, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var hold = PrinterActivity.Begin();
        using var ch = Open(dev, out var path);
        Diag.Log($"Brother: print quality check sheet on '{dev.Name}' via {path}");
        return await PrintQualityCheckAsync(ch, progress, new BrotherTimings(), ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- the logic (testable)

    internal static async Task<BrotherReport> ReadReportAsync(BrotherPjlChannel ch, TimeSpan timeout, CancellationToken ct)
    {
        var text = await ch.AskAsync(SupplyQuery, timeout, ct).ConfigureAwait(false)
                   ?? await ch.AskAsync(SupplyQuery, timeout, ct).ConfigureAwait(false);
        if (text is null) throw new InvalidOperationException("The printer did not answer. Check that it is switched on and connected by USB.");
        var report = BrotherReport.Parse(text);
        if (report.Values.Count < 5) throw new InvalidOperationException("The printer answered, but not with the report this tool understands. It may not be a Brother inkjet.");
        return report;
    }

    internal static async Task<BrotherStatus?> ReadStatusAsync(BrotherPjlChannel ch, TimeSpan timeout, CancellationToken ct) =>
        BrotherStatus.Parse(await ch.AskAsync(StatusQuery, timeout, ct).ConfigureAwait(false));

    /// <summary>The status before a job starts: asked twice when the first question gets no answer (it is a harmless question).</summary>
    static async Task<BrotherStatus?> ReadStatusWithRetryAsync(BrotherPjlChannel ch, TimeSpan timeout, CancellationToken ct) =>
        await ReadStatusAsync(ch, timeout, ct).ConfigureAwait(false) ?? await ReadStatusAsync(ch, timeout, ct).ConfigureAwait(false);

    /// <summary>Refuses to start a job on a printer that is not ready; returns the status otherwise.</summary>
    static async Task<BrotherStatus> RequireReadyAsync(BrotherPjlChannel ch, BrotherTimings t, CancellationToken ct)
    {
        var st = await ReadStatusWithRetryAsync(ch, t.Answer, ct).ConfigureAwait(false)
                 ?? throw new InvalidOperationException("The printer did not answer. Check that it is switched on and connected by USB.");
        if (!st.IsReady) throw new InvalidOperationException($"The printer is not ready (it shows \"{(st.Display.Length > 0 ? st.Display : st.Code)}\"). Deal with that first, then try again.");
        return st;
    }

    internal static async Task<BrotherResult> CleanAsync(BrotherPjlChannel ch, BrotherCleanScope scope, BrotherCleanStrength strength,
        IProgress<string>? progress, BrotherTimings t, CancellationToken ct)
    {
        await RequireReadyAsync(ch, t, ct).ConfigureAwait(false);
        var before = await ReadReportAsync(ch, t.Answer + TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);

        // Brother's driver warns that cleaning with little ink can damage the printer: the cartridge must still have some
        var ink = before.Ink;
        var empty = InksUsed(scope).Where(n => ink.FirstOrDefault(i => i.Name == n) is { Percent: <= 2 and >= 0 }).ToList();
        if (empty.Count > 0)
            throw new InvalidOperationException($"The {string.Join(" and ", empty).ToLowerInvariant()} ink is almost empty. Cleaning with so little ink can damage the printer: replace the cartridge first.");

        var what = Describe(scope, strength);
        progress?.Report($"{what}: sending…");
        await ch.SendAsync(CleanCommand(scope, strength), ct).ConfigureAwait(false);
        Diag.Log($"Brother: sent {CleanCommand(scope, strength)}");

        var expected = ExpectedFor(scope, strength);
        var (finished, sawBusy) = await WaitUntilIdleAsync(ch, what, expected, TimeSpan.FromMinutes(Math.Min(15, expected.TotalMinutes * 4)), progress, t, ct).ConfigureAwait(false);

        var after = await TryReadReportAsync(ch, t, ct).ConfigureAwait(false);
        // Measured on a real MFC-J5955DW: the printer shows "Cleaning" for over a minute, and its cleaning-dot counters move meanwhile (its purge total and "started by hand" tally are
        // only brought up to date minutes later). A cleaning of black alone also fired the colour nozzles: all four colours' counters moved.
        bool counted = after is not null && after.CleaningDots > before.CleaningDots;
        string note = after is null ? "" : counted ? " The printer counted the cleaning." : " The printer's cleaning counter did not move: the command may not have been accepted.";
        if (!finished) return new BrotherResult(false, $"{what}: the printer was still busy after waiting a long time.{note}");
        return new BrotherResult(after is null || counted, $"{what} finished.{note} Print the quality check sheet to see the result.");
    }

    internal static async Task<BrotherResult> PrintQualityCheckAsync(BrotherPjlChannel ch, IProgress<string>? progress, BrotherTimings t, CancellationToken ct)
    {
        await RequireReadyAsync(ch, t, ct).ConfigureAwait(false);
        var before = await ReadReportAsync(ch, t.Answer + TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        progress?.Report("Print quality check sheet: sending…");
        await ch.SendAsync(QualityCheckCommand, ct).ConfigureAwait(false);
        Diag.Log("Brother: sent " + QualityCheckCommand);
        // Measured on a real MFC-J5955DW over three sheets: sometimes the printer stops answering for about twenty seconds while it prints and then shows Ready, sometimes it
        // shows Ready the whole time; its count of check sheets goes up a minute or two after the sheet is out. Either sign counts.
        long? was = before.QualityChecksPrinted;
        var max = TimeSpan.FromSeconds(180) * t.Scale; var settle = TimeSpan.FromSeconds(30) * t.Scale; var expected = TimeSpan.FromSeconds(40) * t.Scale;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool sawBusy = false; int readyInARow = 0, cycle = 0;
        while (sw.Elapsed < max)
        {
            await Task.Delay(t.PollEvery, ct).ConfigureAwait(false);
            var st = await ReadStatusAsync(ch, t.Answer, ct).ConfigureAwait(false);
            bool ready = st is { IsReady: true };
            if (!ready) sawBusy = true;
            readyInARow = ready ? readyInARow + 1 : 0;
            Diag.Log($"Brother: status {(st is null ? "(no answer)" : $"{st.Code} {st.Display}")} after {sw.Elapsed.TotalSeconds:0.0} s");
            int left = (int)Math.Max(0, (expected - sw.Elapsed).TotalSeconds);
            progress?.Report(left > 0 ? $"Print quality check sheet: the printer is working, about {left} s to go. Don't switch it off." : "Print quality check sheet: waiting for the printer to finish…");
            if (++cycle % 3 == 0)
            {
                var r = await TryReadReportAsync(ch, t, ct).ConfigureAwait(false);
                if (r?.QualityChecksPrinted is { } n && was is { } w && n > w) return new BrotherResult(true, "The print quality check sheet is out. The printer counted it.");
            }
            if (sawBusy && readyInARow >= 3 && sw.Elapsed >= settle) return new BrotherResult(true, "The print quality check sheet should be out.");
        }
        return new BrotherResult(false, "No sign that the sheet was printed: the printer neither showed it was busy nor counted a sheet. Check the paper and the printer's screen.");
    }

    static async Task<BrotherReport?> TryReadReportAsync(BrotherPjlChannel ch, BrotherTimings t, CancellationToken ct)
    {
        try { return await ReadReportAsync(ch, t.Answer + TimeSpan.FromSeconds(4), ct).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// After a maintenance command the printer shows busy (a status such as "Cleaning", or no answer at all) and then "Ready" again. Waits for that.
    /// Returns Finished = false when the printer was still busy at <paramref name="max"/>, and whether it was ever seen busy.
    /// </summary>
    static async Task<(bool Finished, bool SawBusy)> WaitUntilIdleAsync(BrotherPjlChannel ch, string what, TimeSpan expected, TimeSpan max, IProgress<string>? progress, BrotherTimings t, CancellationToken ct)
    {
        expected *= t.Scale; max *= t.Scale;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool sawBusy = false; int readyInARow = 0;
        while (sw.Elapsed < max)
        {
            await Task.Delay(t.PollEvery, ct).ConfigureAwait(false);
            var st = await ReadStatusAsync(ch, t.Answer, ct).ConfigureAwait(false);
            bool ready = st is { IsReady: true };
            if (!ready) sawBusy = true;           // no answer at all usually means it is too busy to talk
            readyInARow = ready ? readyInARow + 1 : 0;
            Diag.Log($"Brother: status {(st is null ? "(no answer)" : $"{st.Code} {st.Display}")} after {sw.Elapsed.TotalSeconds:0.0} s");
            int left = (int)Math.Max(0, (expected - sw.Elapsed).TotalSeconds);
            var shows = st is { IsReady: false, Display.Length: > 0 } ? $" (the printer shows \"{st.Display}\")" : "";
            progress?.Report(left > 0 ? $"{what}: the printer is working, about {left} s to go{shows}. Don't switch it off." : $"{what}: still working{shows}. Don't switch it off.");

            if (readyInARow >= 3 && (sawBusy || sw.Elapsed >= t.NoBusyGrace * t.Scale)) return (true, sawBusy);
        }
        return (false, sawBusy);
    }
}
