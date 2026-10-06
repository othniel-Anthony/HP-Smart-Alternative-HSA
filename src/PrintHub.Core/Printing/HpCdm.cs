using PrintHub.Core.Ipp;
using System.Text;
using System.Text.Json;

namespace PrintHub.Core.Printing;

/// <summary>
/// HP's newer web services ("CDM": JSON over HTTP under /cdm, used by current Smart Tank, DeskJet, ENVY, OfficeJet and LaserJet models).
/// The printer's own web page is built on it, so the paths and bodies here are the ones that page uses: reports and cleaning pages are
/// printed by PATCHing /cdm/report/v1/print, alignment goes through /cdm/calibration/v1/calibration/penAlignSemiauto, and ink levels come
/// from /cdm/supply/v1/suppliesPublic. Older printers answer to LEDM (<see cref="HpMaintenance"/>) instead.
/// </summary>
public static class HpCdm
{
    public const string ReportsPath = "cdm/report/v1/reports";
    public const string PrintPath = "cdm/report/v1/print";
    public const string SuppliesPath = "cdm/supply/v1/suppliesPublic";
    public const string CalibrationCapsPath = "cdm/calibration/v1/capabilities";
    public const string CalibrationPath = "cdm/calibration/v1/calibration/penAlignSemiauto";
    public const string AlertsPath = "cdm/alert/v1/alerts";
    public const string SystemStatusPath = "cdm/system/v1/status";

    // ---------------------------------------------------------------- reports and cleaning

    // cleaningPage / cleaningPageLevel2 / cleaningPageLevel3 are the three cleaning levels the printer lists (printable:false = no paper output of their own)
    static readonly Dictionary<string, (string Title, HpJobKind Kind, int Level)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cleaningPage"] = ("Level 1: light cleaning", HpJobKind.Clean, 1),
        ["cleaningPageLevel2"] = ("Level 2: medium cleaning", HpJobKind.Clean, 2),
        ["cleaningPageLevel3"] = ("Level 3: deep cleaning", HpJobKind.Clean, 3),
        ["cleaningVerificationPage"] = ("Cleaning check page", HpJobKind.Report, 0),
        ["printQualityTestReport"] = ("Print quality report", HpJobKind.Report, 0),
        ["configurationReport"] = ("Printer status report", HpJobKind.Report, 0),
        ["diagnosticsReport"] = ("Diagnostics report", HpJobKind.Report, 0),
        ["alignmentPage"] = ("Alignment page", HpJobKind.Report, 0),
        ["extendedConfigurationPage"] = ("Extended self-test page", HpJobKind.Report, 0),
        ["networkConfigurationReport"] = ("Network configuration report", HpJobKind.Report, 0),
        ["wirelessNetworkPage"] = ("Wireless test report", HpJobKind.Report, 0),
        ["paperFeedCleaningPage"] = ("Paper feed cleaning page", HpJobKind.Report, 0),
        ["ribSmearCleaningPage"] = ("Smear cleaning page", HpJobKind.Report, 0),
    };

    // report pages worth offering first, in this order; the rest follow alphabetically
    static readonly string[] Order = { "printQualityTestReport", "cleaningVerificationPage", "alignmentPage", "configurationReport", "diagnosticsReport", "extendedConfigurationPage" };

    public static List<HpJob> ParseReports(string json)
    {
        var jobs = new List<HpJob>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Array) return jobs;
            foreach (var r in reports.EnumerateArray())
            {
                var id = Str(r, "reportId"); if (id is null) continue;
                bool printable = string.Equals(Str(r, "printable"), "true", StringComparison.OrdinalIgnoreCase);
                if (Known.TryGetValue(id, out var k)) jobs.Add(new HpJob(id, k.Title, k.Kind, k.Level, HpProtocol.Cdm));
                else if (printable && !id.Contains("privacy", StringComparison.OrdinalIgnoreCase) && !id.Contains("clean", StringComparison.OrdinalIgnoreCase))
                    jobs.Add(new HpJob(id, Str(r, "localizedName") ?? HpMaintenance.Humanize(id), HpJobKind.Report, 0, HpProtocol.Cdm));
                // anything else (an unknown non-printing "report", an unknown cleaning variant) is not offered
            }
        }
        catch (JsonException) { }
        int Rank(HpJob j) { var i = Array.FindIndex(Order, o => o.Equals(j.JobType, StringComparison.OrdinalIgnoreCase)); return i < 0 ? Order.Length : i; }
        return jobs.OrderBy(j => j.Kind == HpJobKind.Clean ? 0 : 1).ThenBy(Rank).ThenBy(j => j.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"semiAutomatic" when the printer lists the scanner-assisted alignment (the only kind CDM printers offer), otherwise null.</summary>
    public static string? ParseAlignmentMode(string capabilitiesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(capabilitiesJson);
            if (doc.RootElement.TryGetProperty("availableCalibrations", out var a) && a.ValueKind == JsonValueKind.Array &&
                a.EnumerateArray().Any(x => string.Equals(x.GetString(), "penAlignSemiauto", StringComparison.OrdinalIgnoreCase)))
                return "semiAutomatic";
        }
        catch (JsonException) { }
        return null;
    }

    public static async Task<HpMaintenanceInfo> ProbeAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        string? reports = await GetOrNullAsync(http, new Uri(baseUri, ReportsPath), ct).ConfigureAwait(false);
        if (reports is null) return new HpMaintenanceInfo { Problem = "The printer did not answer HP's web-services requests, so there is nothing to offer here." };
        var jobs = ParseReports(reports);
        string? caps = await GetOrNullAsync(http, new Uri(baseUri, CalibrationCapsPath), ct).ConfigureAwait(false);
        return new HpMaintenanceInfo { Reachable = true, Protocol = HpProtocol.Cdm, Jobs = jobs, AlignmentMode = caps is null ? null : ParseAlignmentMode(caps) };
    }

    // ---------------------------------------------------------------- ink levels

    // supply types that are shown as a level; printheads ("inkCartridge" on a tank printer, which has no level) fall out because they carry no percentage
    static readonly string[] LevelTypes = { "inkTank", "inkCartridge", "ink", "toner", "tonerCartridge", "cartridge", "rechargeableToner" };

    public static List<SupplyLevel> ParseSupplies(string json)
    {
        var result = new List<SupplyLevel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("suppliesList", out var list) || list.ValueKind != JsonValueKind.Array) return result;
            foreach (var s in list.EnumerateArray())
            {
                var type = Str(s, "supplyType") ?? "";
                if (!LevelTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) continue;
                var state = Str(s, "supplyState") ?? "";
                if (state.Equals("missing", StringComparison.OrdinalIgnoreCase)) continue;

                int percent = Int(s, "percentLifeDisplay") ?? Int(s, "percentLifeRemaining") ?? -1;
                if (percent < 0 && !type.Equals("inkTank", StringComparison.OrdinalIgnoreCase) && !type.Contains("toner", StringComparison.OrdinalIgnoreCase)) continue; // a printhead or cartridge with no level: nothing to show
                percent = percent < 0 ? -1 : Math.Clamp(percent, 0, 100);

                var colors = s.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Array ? string.Concat(c.EnumerateArray().Select(x => x.GetString())) : (Str(s, "supplyColorCode") ?? "");
                var (name, color) = LedmClient.Describe(colors, null);
                result.Add(new SupplyLevel(name, type.Contains("toner", StringComparison.OrdinalIgnoreCase) ? "toner" : "ink", color, percent, 10, 100));
            }
        }
        catch (JsonException) { }
        return result;
    }

    public static async Task<List<SupplyLevel>?> GetSuppliesAsync(Uri baseUri, HttpClient http, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var json = await GetOrNullAsync(http, new Uri(baseUri, SuppliesPath), cts.Token).ConfigureAwait(false);
        if (json is null) return null;
        var list = ParseSupplies(json);
        return list.Count > 0 ? list : null;
    }

    // ---------------------------------------------------------------- doing things

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToString() : null) : null;
    static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static async Task<string?> GetOrNullAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            using var r = await http.GetAsync(uri, ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; return null; }
    }

    static async Task<string?> PrintStateAsync(HttpClient http, Uri baseUri, string path, CancellationToken ct)
    {
        var json = await GetOrNullAsync(http, new Uri(baseUri, path), ct).ConfigureAwait(false);
        if (json is null) return null;
        try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement, "state"); } catch (JsonException) { return null; }
    }

    /// <summary>How the last job on this endpoint ended ("success", "failure", "cancelled"...); null when the printer does not say.</summary>
    static async Task<string?> LastResultAsync(HttpClient http, Uri baseUri, string path, CancellationToken ct)
    {
        var json = await GetOrNullAsync(http, new Uri(baseUri, path), ct).ConfigureAwait(false);
        if (json is null) return null;
        try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement, "lastResult"); } catch (JsonException) { return null; }
    }

    /// <summary>A reason the printer gives for not printing (empty tray, jam...), from its alert list; null when it lists none.</summary>
    public static async Task<string?> GetAlertTextAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        var json = await GetOrNullAsync(http, new Uri(baseUri, AlertsPath), ct).ConfigureAwait(false);
        if (json is null) return null;
        Diag.Log("HP CDM alerts: " + json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("alerts", out var a) || a.ValueKind != JsonValueKind.Array) return null;
            var names = a.EnumerateArray().Select(x => Str(x, "category") ?? Str(x, "alertId") ?? Str(x, "stringId")).Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (names.Count == 0) return null;
            if (names.Any(n => n!.Contains("tray", StringComparison.OrdinalIgnoreCase) || n.Contains("media", StringComparison.OrdinalIgnoreCase) || n.Contains("paper", StringComparison.OrdinalIgnoreCase)))
                return "The printer reports a paper problem (" + string.Join(", ", names) + "). Load plain paper, close the tray and try again.";
            return "The printer reports: " + string.Join(", ", names) + ".";
        }
        catch (JsonException) { return null; }
    }

    static async Task PatchAsync(HttpClient http, Uri uri, string json, string what, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        Diag.Log($"HP CDM: {what} -> HTTP {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode)
        {
            var why = await GetAlertTextAsync(http, uri, ct).ConfigureAwait(false);
            throw new InvalidOperationException(why ?? $"The printer refused “{what}” (HTTP {(int)resp.StatusCode}). It may not support it, or it is busy.");
        }
    }

    /// <summary>Print a report or cleaning page and wait for the printer to finish it. Returns "finished" or "sent".</summary>
    public static async Task<string> RunReportAsync(HttpClient http, Uri baseUri, HpJob job, IProgress<string>? progress, CancellationToken ct,
        TimeSpan? maxWait = null, TimeSpan? poll = null, TimeSpan? startWindow = null)
    {
        var wait = maxWait ?? TimeSpan.FromMinutes(job.Kind == HpJobKind.Clean ? 12 : 4);
        var every = poll ?? TimeSpan.FromSeconds(2);
        var window = startWindow ?? TimeSpan.FromSeconds(25);

        var before = await PrintStateAsync(http, baseUri, PrintPath, ct).ConfigureAwait(false);
        if (string.Equals(before, "processing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The printer is busy with another report. Wait for it to finish and try again.");

        string version = "1.0.0";
        if (await GetOrNullAsync(http, new Uri(baseUri, ReportsPath), ct).ConfigureAwait(false) is { } list)
        {
            try
            {
                using var doc = JsonDocument.Parse(list);
                foreach (var r in doc.RootElement.GetProperty("reports").EnumerateArray())
                    if (string.Equals(Str(r, "reportId"), job.JobType, StringComparison.OrdinalIgnoreCase)) { version = Str(r, "version") ?? version; break; }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }

        var body = JsonSerializer.Serialize(new { version, reportId = job.JobType, state = "processing" });
        await PatchAsync(http, new Uri(baseUri, PrintPath), body, job.Title, ct).ConfigureAwait(false);

        var started = DateTime.UtcNow; bool sawWork = false; int idleInARow = 0;
        while (DateTime.UtcNow - started < wait)
        {
            await Task.Delay(every, ct).ConfigureAwait(false);
            var state = await PrintStateAsync(http, baseUri, PrintPath, ct).ConfigureAwait(false);
            Diag.Log($"HP CDM: print state {state ?? "(no answer)"} after {(DateTime.UtcNow - started).TotalSeconds:0} s");
            if (state is null) continue;

            if (string.Equals(state, "processing", StringComparison.OrdinalIgnoreCase)) { sawWork = true; idleInARow = 0; progress?.Report("The printer is working…"); continue; }
            idleInARow++;
            if (sawWork && idleInARow >= 2)
            {
                var result = await LastResultAsync(http, baseUri, PrintPath, ct).ConfigureAwait(false);
                Diag.Log("HP CDM: last result " + (result ?? "(none)"));
                if (result is not null && !result.Equals("success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(await GetAlertTextAsync(http, baseUri, ct).ConfigureAwait(false) ?? $"The printer ended “{job.Title}” with the result “{result}”. Check the paper and the printer's lights, then try again.");
                return "finished";
            }
            if (!sawWork && DateTime.UtcNow - started > window)
            {
                var why = await GetAlertTextAsync(http, baseUri, ct).ConfigureAwait(false);
                if (job.Kind == HpJobKind.Clean)
                    throw new InvalidOperationException(why ?? $"The printer did not react to “{job.Title}”: it stayed idle. Check that it has paper and no errors, or use HP Smart or the printer's own menu.");
                if (why is not null) throw new InvalidOperationException(why);
                return "sent"; // a short page may simply have been too quick to catch
            }
        }
        throw new TimeoutException("The printer took too long to finish. Check it for errors.");
    }

    /// <summary>
    /// Second step of the scanner-assisted printhead alignment: the alignment page (the "alignmentPage" report, printed first) is lying face down on
    /// the scanner glass, and the printer scans and measures it. Started with nothing on the glass, the printer fails it after about 18 s.
    /// Waits for the outcome (up to <paramref name="maxWait"/>) and reports it.
    /// </summary>
    public static async Task<string> ScanAlignmentAsync(HttpClient http, Uri baseUri, IProgress<string>? progress, CancellationToken ct,
        TimeSpan? maxWait = null, TimeSpan? poll = null)
    {
        var every = poll ?? TimeSpan.FromSeconds(3);
        var before = await PrintStateAsync(http, baseUri, CalibrationPath, ct).ConfigureAwait(false);
        if (before is "processing" or "pending")
            throw new InvalidOperationException("An earlier alignment has not finished. Wait for the printer, or switch it off and on.");
        var body = JsonSerializer.Serialize(new { calibrationType = "penAlignSemiauto", operationType = "calibration" });
        await PatchAsync(http, new Uri(baseUri, CalibrationPath), body, "alignment", ct).ConfigureAwait(false);

        progress?.Report("The printer is scanning the alignment page…");
        var started = DateTime.UtcNow; bool sawWork = false; int idle = 0;
        while (DateTime.UtcNow - started < (maxWait ?? TimeSpan.FromMinutes(8)))
        {
            await Task.Delay(every, ct).ConfigureAwait(false);
            var json = await GetOrNullAsync(http, new Uri(baseUri, CalibrationPath), ct).ConfigureAwait(false);
            if (json is null) continue;
            string? state = null, result = null;
            try { using var doc = JsonDocument.Parse(json); state = Str(doc.RootElement, "state"); result = Str(doc.RootElement, "lastResult"); } catch (JsonException) { continue; }
            Diag.Log($"HP CDM: alignment state {state} result {result ?? "-"} after {(DateTime.UtcNow - started).TotalSeconds:0} s");

            if (state is "processing" or "pending") { sawWork = true; idle = 0; continue; }
            if (++idle < 2 || (!sawWork && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))) continue;
            if (result is not null && !result.Equals("success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(await GetAlertTextAsync(http, baseUri, ct).ConfigureAwait(false) ?? $"The printer could not read the alignment page (“{result}”). Put the printed page face down on the scanner glass, close the lid and try again.");
            return "The alignment finished. Print a test page to check the result.";
        }
        throw new TimeoutException("The printer took too long to finish the alignment. Check it for errors.");
    }
}
