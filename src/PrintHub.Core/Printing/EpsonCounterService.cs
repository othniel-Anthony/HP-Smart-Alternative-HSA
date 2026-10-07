using System.Text;
using System.Text.Json;
using PrintHub.Core.Discovery;
using PrintHub.Core.Settings;

namespace PrintHub.Core.Printing;

/// <summary>Where EPSON-CTRL messages go: a real D4 session over USB, or a simulated printer in tests.</summary>
internal interface IEpsonControl
{
    Task<byte[]?> RequestAsync(byte[] frame, CancellationToken ct);
}

internal sealed class D4Control : IEpsonControl
{
    readonly EpsonD4Session _s;
    public D4Control(EpsonD4Session s) => _s = s;
    public Task<byte[]?> RequestAsync(byte[] frame, CancellationToken ct) => _s.RequestAsync(frame, ct);
}

public sealed record EpsonCounterReading(string Description, string Kind, int Value, int Max)
{
    /// <summary>0-100+ ; above 100 means past the limit the manufacturer set.</summary>
    public int Percent => Max <= 0 ? 0 : (int)Math.Min(999, Math.Round(100.0 * Value / Max));
}

public sealed record EpsonPlannedWrite(int Address, int Current, int Target);

public sealed class EpsonAnalysis
{
    public string DeviceName { get; init; } = "";
    /// <summary>The model name the printer itself reported (IEEE 1284 "MDL").</summary>
    public string? ReportedModel { get; init; }
    public string? ModelKey { get; init; }
    public EpsonModelSpec? Spec { get; init; }
    public List<EpsonCounterReading> Counters { get; init; } = new();
    public List<EpsonPlannedWrite> Plan { get; init; } = new();
    /// <summary>Why nothing can be done (model unknown, key refused, ...), or null.</summary>
    public string? Problem { get; init; }
    public bool CanReset => Problem is null && Spec is not null;
    public bool NothingToChange => CanReset && Plan.Count == 0;
}

public sealed class EpsonResetResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = "";
    public string? BackupPath { get; init; }
}

/// <summary>
/// Reads and resets the waste-ink counters of an Epson printer over USB, using a model database the user provides.
/// Everything is read first, a backup is saved before the first write, every write is read back, and a failed write is rolled back.
/// </summary>
public static class EpsonCounterService
{
    public static string DatabasePath => Path.Combine(AppPaths.DataDir, "epson-database.json");
    public static string BackupFolder => Path.Combine(AppPaths.DataDir, "epson-backups");

    /// <summary>Where HSA looks for the database, in order: its data folder, then the folder the program runs from.</summary>
    public static IEnumerable<string> DatabaseCandidates()
    {
        yield return DatabasePath;
        yield return Path.Combine(AppContext.BaseDirectory, "epson-database.json");
    }

    public static string? FindDatabase() => DatabaseCandidates().FirstOrDefault(File.Exists);

    static (string Path, DateTime Stamp, EpsonDatabase Db)? _cache;

    /// <summary>
    /// Load the database: a file the user put in HSA's data folder or next to the program wins (so a newer database can replace the built-in one),
    /// otherwise the database compiled into this build. Nothing to click. The parsed file is reused until it changes on disk.
    /// </summary>
    public static EpsonDatabase? TryLoadDatabase(out string? error)
    {
        error = null;
        var path = FindDatabase();
        if (path is not null)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cache is { } c && c.Path == path && c.Stamp == stamp) return c.Db;
                var db = EpsonDatabase.Load(path);
                _cache = (path, stamp, db);
                return db;
            }
            catch (Exception ex)
            {
                error = $"{Path.GetFileName(path)}: {ex.Message}"; // a broken override must not hide the built-in database
                Diag.Log("Epson database file could not be read: " + error);
            }
        }
        try
        {
            if (_embedded is null && EpsonDatabase.HasEmbedded) _embedded = EpsonDatabase.LoadEmbedded();
            if (_embedded is not null) return _embedded;
        }
        catch (Exception ex) { error ??= "built-in database: " + ex.Message; }
        return null;
    }

    static EpsonDatabase? _embedded;

    // ---------------------------------------------------------------- public entry points (real printer)

    public static async Task<EpsonAnalysis> AnalyzeAsync(PrinterDevice dev, EpsonDatabase db, CancellationToken ct = default)
    {
        return await WithSessionAsync(dev, ctrl => AnalyzeCoreAsync(ctrl, dev.Name, db, ct), ct).ConfigureAwait(false);
    }

    public static async Task<EpsonResetResult> ResetAsync(PrinterDevice dev, EpsonDatabase db, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await WithSessionAsync(dev, async ctrl =>
        {
            var a = await AnalyzeCoreAsync(ctrl, dev.Name, db, ct).ConfigureAwait(false);
            return await ResetCoreAsync(ctrl, a, BackupFolder, progress, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Put the cells back to what the newest backup for this model recorded.</summary>
    public static async Task<EpsonResetResult> RestoreLatestAsync(PrinterDevice dev, EpsonDatabase db, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await WithSessionAsync(dev, async ctrl =>
        {
            var a = await AnalyzeCoreAsync(ctrl, dev.Name, db, ct).ConfigureAwait(false);
            if (!a.CanReset) return new EpsonResetResult { Summary = a.Problem ?? "Cannot continue." };
            var file = LatestBackup(a.Spec!.Key);
            if (file is null) return new EpsonResetResult { Summary = "There is no saved backup for this model." };
            return await RestoreCoreAsync(ctrl, a.Spec!, file, progress, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public static string? LatestBackup(string modelKey)
    {
        if (!Directory.Exists(BackupFolder)) return null;
        return Directory.GetFiles(BackupFolder, $"{Safe(modelKey)}_*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    /// <summary>
    /// Asks an Epson plugged in by USB for its status block (state, error and the ink left in each cartridge). It is the same short conversation the waste counters use
    /// (a D4 session over the print interface), one question long. Null when the printer does not answer with a status block.
    /// </summary>
    public static async Task<EpsonStatusReading?> ReadStatusAsync(PrinterDevice dev, CancellationToken ct = default)
    {
        return await WithSessionAsync(dev, async ctrl =>
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var reply = await ctrl.RequestAsync(EpsonCtrl.StatusFrame(), ct).ConfigureAwait(false);
                var status = EpsonStatusReading.Parse(reply);
                if (status is not null) return status;
                Diag.Log($"Epson status: no complete status block (attempt {attempt + 1}, {reply?.Length ?? 0} bytes)");
            }
            return null;
        }, ct).ConfigureAwait(false);
    }

    static async Task<T> WithSessionAsync<T>(PrinterDevice dev, Func<IEpsonControl, Task<T>> work, CancellationToken ct)
    {
        using var hold = PrinterActivity.Begin();   // HSA's own searches and status requests wait until the printer is free again
        using var stream = EpsonMaintenance.OpenUsbStream(dev, out var path);
        using var d4 = new EpsonD4Session(stream) { Trace = s => Diag.Log("Epson D4 " + (s.Length > 140 ? s[..140] + "…" : s)) };
        Diag.Log($"Epson counters: opening {path}");
        await d4.ConnectAsync(ct).ConfigureAwait(false);
        try { return await work(new D4Control(d4)).ConfigureAwait(false); }
        finally { await d4.LeaveAsync().ConfigureAwait(false); }
    }

    // ---------------------------------------------------------------- the logic (testable)

    internal static async Task<int?> ReadCellAsync(IEpsonControl ctrl, EpsonModelSpec spec, int address, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var reply = await ctrl.RequestAsync(EpsonCtrl.ReadFrame(spec.ReadKeyBytes, address), ct).ConfigureAwait(false);
            if (EpsonCtrl.ParseRead(reply) is { } r && r.Address == address) return r.Value;
        }
        return null;
    }

    /// <summary>Write one cell and read it back. The printer may answer OK and still keep the old value, so the read-back is what counts.</summary>
    internal static async Task<bool> WriteCellAsync(IEpsonControl ctrl, EpsonModelSpec spec, int address, int value, CancellationToken ct)
    {
        var reply = await ctrl.RequestAsync(EpsonCtrl.WriteFrame(spec.ReadKeyBytes, spec.WriteKey, address, value), ct).ConfigureAwait(false);
        if (!EpsonCtrl.WriteConfirmed(reply)) { Diag.Log($"Epson: write 0x{address:X4} was not confirmed"); return false; }
        var after = await ReadCellAsync(ctrl, spec, address, ct).ConfigureAwait(false);
        if (after != value) { Diag.Log($"Epson: 0x{address:X4} reads back {after?.ToString() ?? "nothing"} after writing {value}"); return false; }
        return true;
    }

    internal static async Task<EpsonAnalysis> AnalyzeCoreAsync(IEpsonControl ctrl, string deviceName, EpsonDatabase db, CancellationToken ct)
    {
        var idReply = await ctrl.RequestAsync(EpsonCtrl.DeviceIdFrame(), ct).ConfigureAwait(false);
        var reported = EpsonCtrl.ParseModel(idReply);
        Diag.Log($"Epson counters: printer reports model '{reported}', Windows name '{deviceName}'");

        EpsonAnalysis Fail(string problem, EpsonModelSpec? spec = null, string? key = null) =>
            new() { DeviceName = deviceName, ReportedModel = reported, Spec = spec, ModelKey = key, Problem = problem };

        // the printer's own model name first; the Windows queue name must not disagree with it
        var byPrinter = reported is null ? null : db.Find(reported);
        var byName = db.Find(deviceName);
        var match = byPrinter?.Spec is not null ? byPrinter : byName;
        if (byPrinter?.Spec is not null && byName.Spec is not null && byPrinter.Spec.Fingerprint != byName.Spec.Fingerprint)
            return Fail($"The printer says it is a “{reported}” but Windows calls it “{deviceName}”, and the database treats those as different models. HSA will not guess.");
        if (match.Spec is null) return Fail(match.Problem ?? "This printer's model is not in the database.");
        if (match.Problem is not null) return Fail(match.Problem, match.Spec, match.ModelKey);

        var spec = match.Spec;
        var cells = new Dictionary<int, int>();
        foreach (var addr in spec.AllAddresses.OrderBy(a => a))
        {
            ct.ThrowIfCancellationRequested();
            var v = await ReadCellAsync(ctrl, spec, addr, ct).ConfigureAwait(false);
            if (v is null)
                return Fail(cells.Count == 0
                    ? "The printer did not accept this model's access key, so the database entry does not fit this printer (or its firmware). Nothing was changed."
                    : $"The printer stopped answering while reading memory cell 0x{addr:X4}. Nothing was changed.", spec, match.ModelKey);
            cells[addr] = v.Value;
        }

        var readings = new List<EpsonCounterReading>();
        foreach (var g in spec.PadGroups)
            foreach (var c in g.Counters)
            {
                long value = 0;
                for (int i = 0; i < c.Cells.Count; i++) value += (long)cells.GetValueOrDefault(c.Cells[i]) << (8 * i);
                foreach (var (addr, mask, weight) in c.Extras)
                {
                    int shift = mask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(mask);
                    value += (long)((cells.GetValueOrDefault(addr) & mask) >> shift) * weight;
                }
                readings.Add(new EpsonCounterReading(c.Description, g.Kind, (int)Math.Min(value, int.MaxValue), c.Max));
            }
        if (readings.Count == 0) // models without a counter formula: show the raw state instead of nothing
            foreach (var g in spec.PadGroups)
                readings.Add(new EpsonCounterReading(g.Description, g.Kind, g.Addresses.Count(a => cells[a] != g.ResetValues[g.Addresses.IndexOf(a)]), 0));

        var plan = new List<EpsonPlannedWrite>();
        foreach (var g in spec.PadGroups)
            for (int i = 0; i < g.Addresses.Count; i++)
                if (cells[g.Addresses[i]] != g.ResetValues[i]) plan.Add(new EpsonPlannedWrite(g.Addresses[i], cells[g.Addresses[i]], g.ResetValues[i]));
        foreach (var c in spec.Close)
        {
            int target = cells[c.Address] & c.AndMask;
            if (target != cells[c.Address]) plan.Add(new EpsonPlannedWrite(c.Address, cells[c.Address], target));
        }

        return new EpsonAnalysis { DeviceName = deviceName, ReportedModel = reported, ModelKey = match.ModelKey, Spec = spec, Counters = readings, Plan = plan };
    }

    internal static async Task<EpsonResetResult> ResetCoreAsync(IEpsonControl ctrl, EpsonAnalysis a, string backupFolder, IProgress<string>? progress, CancellationToken ct)
    {
        if (!a.CanReset) return new EpsonResetResult { Summary = a.Problem ?? "Cannot continue." };
        var spec = a.Spec!;

        // 1. read everything and save it before anything is written
        progress?.Report("Reading the printer's memory…");
        var original = new SortedDictionary<int, int>();
        foreach (var addr in spec.AllAddresses)
        {
            var v = await ReadCellAsync(ctrl, spec, addr, ct).ConfigureAwait(false);
            if (v is null) return new EpsonResetResult { Summary = $"Could not read memory cell 0x{addr:X4}. Nothing was changed." };
            original[addr] = v.Value;
        }
        string backup;
        try { backup = SaveBackup(backupFolder, spec.Key, a, original); }
        catch (Exception ex) { return new EpsonResetResult { Summary = "Could not save the backup (" + ex.Message + "), so nothing was changed." }; }

        // 2. what has to change
        var targets = new List<(int Address, int Target)>();
        foreach (var g in spec.PadGroups)
            for (int i = 0; i < g.Addresses.Count; i++) targets.Add((g.Addresses[i], g.ResetValues[i]));
        foreach (var c in spec.Close) targets.Add((c.Address, original[c.Address] & c.AndMask));

        // 3. write, reading back each cell; undo everything if one write does not stick
        var changed = new List<int>();
        int n = 0;
        foreach (var (addr, target) in targets.DistinctBy(t => t.Address))
        {
            ct.ThrowIfCancellationRequested();
            n++;
            if (original[addr] == target) continue;
            progress?.Report($"Writing {n} of {targets.Count}…");
            if (await WriteCellAsync(ctrl, spec, addr, target, ct).ConfigureAwait(false)) { changed.Add(addr); continue; }

            progress?.Report("A write was refused. Putting the changes back…");
            int undone = 0;
            foreach (var done in changed.AsEnumerable().Reverse().Concat(new[] { addr }))
                if (await WriteCellAsync(ctrl, spec, done, original[done], CancellationToken.None).ConfigureAwait(false)) undone++;
            bool restored = undone == changed.Count + 1;
            return new EpsonResetResult
            {
                BackupPath = backup,
                Summary = $"The printer would not accept the change at memory cell 0x{addr:X4}. " +
                          (restored ? "Everything that had been written was put back; the printer is as it was." : $"Only {undone} of {changed.Count + 1} cells could be put back; the original values are in the backup: {backup}"),
            };
        }

        // 4. check the final state
        progress?.Report("Checking the result…");
        foreach (var (addr, target) in targets.DistinctBy(t => t.Address))
        {
            var v = await ReadCellAsync(ctrl, spec, addr, ct).ConfigureAwait(false);
            if (v != target)
                return new EpsonResetResult { BackupPath = backup, Summary = $"The final check failed at cell 0x{addr:X4} (read {v?.ToString() ?? "nothing"}, expected {target}). The original values are saved in {backup}." };
        }

        return new EpsonResetResult
        {
            Success = true, BackupPath = backup,
            Summary = changed.Count == 0 ? "Everything already had the reset values." : $"Reset done: {changed.Count} memory cell{(changed.Count == 1 ? "" : "s")} changed and checked. Switch the printer off and on again.",
        };
    }

    internal static async Task<EpsonResetResult> RestoreCoreAsync(IEpsonControl ctrl, EpsonModelSpec spec, string backupFile, IProgress<string>? progress, CancellationToken ct)
    {
        Dictionary<int, int> cells;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(backupFile));
            cells = doc.RootElement.GetProperty("cells").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16), p => p.Value.GetInt32());
        }
        catch (Exception ex) { return new EpsonResetResult { Summary = "The backup file could not be read: " + ex.Message }; }
        if (cells.Keys.Any(a => !spec.AllAddresses.Contains(a))) return new EpsonResetResult { Summary = "That backup does not belong to this model." };

        int changed = 0, n = 0;
        foreach (var (addr, value) in cells.OrderBy(c => c.Key))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Restoring {++n} of {cells.Count}…");
            var current = await ReadCellAsync(ctrl, spec, addr, ct).ConfigureAwait(false);
            if (current == value) continue;
            if (!await WriteCellAsync(ctrl, spec, addr, value, ct).ConfigureAwait(false))
                return new EpsonResetResult { Summary = $"The printer would not accept the restore at cell 0x{addr:X4}." };
            changed++;
        }
        return new EpsonResetResult { Success = true, BackupPath = backupFile, Summary = changed == 0 ? "The printer already matches the backup." : $"Restored {changed} memory cell{(changed == 1 ? "" : "s")} from the backup. Switch the printer off and on again." };
    }

    static string SaveBackup(string folder, string modelKey, EpsonAnalysis a, IDictionary<int, int> cells)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Safe(modelKey)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        var json = new
        {
            time = DateTime.Now.ToString("s"), model = modelKey, windowsName = a.DeviceName, printerReportedModel = a.ReportedModel,
            cells = cells.OrderBy(c => c.Key).ToDictionary(c => $"0x{c.Key:X4}", c => c.Value),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return path;
    }
}
