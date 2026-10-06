using System.Net;
using System.Text;
using System.Xml.Linq;

namespace PrintHub.Core.Printing;

public enum HpJobKind { Clean, Report }

/// <summary>One maintenance print job the printer says it can do (from its InternalPrintCap document).</summary>
public sealed record HpJob(string JobType, string Title, HpJobKind Kind, int Level = 0, HpProtocol Protocol = HpProtocol.Ledm);

/// <summary>Which of HP's web-service dialects a printer speaks: LEDM (XML, older and many network printers) or CDM (JSON, current models).</summary>
public enum HpProtocol { Ledm, Cdm }

public sealed class HpMaintenanceInfo
{
    /// <summary>The printer answered the web-services requests at all.</summary>
    public bool Reachable { get; init; }
    public string? Problem { get; init; }
    public HpProtocol Protocol { get; init; }
    public List<HpJob> Jobs { get; init; } = new();
    /// <summary>"automatic", "semiAutomatic" or "manual" when the printer supports alignment; otherwise null.</summary>
    public string? AlignmentMode { get; init; }
    public bool CanAlign => AlignmentMode is not null;
    public IEnumerable<HpJob> CleaningJobs => Jobs.Where(j => j.Kind == HpJobKind.Clean && j.Level > 0).OrderBy(j => j.Level);
    public IEnumerable<HpJob> Reports => Jobs.Where(j => j.Kind == HpJobKind.Report);
}

/// <summary>
/// Maintenance for HP printers that offer HP's web services ("LEDM": XML over HTTP, served on the network and over the USB web-services
/// interface): cleaning levels, printhead alignment and report pages, as in HP Smart. What is offered comes from the printer itself.
/// The request formats follow the open-source HPLIP project (base/maint.py, base/status.py).
/// </summary>
public static class HpMaintenance
{
    public const string InternalPrintPath = "DevMgmt/InternalPrintDyn.xml";
    public const string InternalPrintCapPath = "DevMgmt/InternalPrintCap.xml";
    public const string StatusPath = "DevMgmt/ProductStatusDyn.xml";
    public const string ConsumablesPath = "DevMgmt/ConsumableConfigDyn.xml";
    public const string CalibrationStatePath = "Calibration/State";
    public const string CalibrationSessionPath = "Calibration/Session";

    const string JobXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!--  THIS DATA SUBJECT TO DISCLAIMER(S)INCLUDED WITH THE PRODUCT OF ORIGIN. -->\n" +
        "<ipcap:InternalPrintCap xmlns:ipcap=\"http://www.hp.com/schemas/imaging/con/ledm/internalprintcap/2008/03/21\" " +
        "xmlns:ipdyn=\"http://www.hp.com/schemas/imaging/con/ledm/internalprintdyn/2008/03/21\" " +
        "xmlns:dd=\"http://www.hp.com/schemas/imaging/con/dictionaries/1.0/\" " +
        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
        "xsi:schemaLocation=\"http://www.hp.com/schemas/imaging/con/ledm/internalprintcap/2008/03/21 ../schemas/InternalPrintCap.xsd " +
        "http://www.hp.com/schemas/imaging/con/ledm/internalprintdyn/2008/03/21 ../schemas/InternalPrintDyn.xsd " +
        "http://www.hp.com/schemas/imaging/con/dictionaries/1.0/ ../schemas/dd/DataDictionaryMasterLEDM.xsd\">\n" +
        "<ipdyn:JobType>{0}</ipdyn:JobType>\n</ipcap:InternalPrintCap>";

    const string CalibrationXml =
        "<cal:CalibrationState xmlns:cal=\"http://www.hp.com/schemas/imaging/con/cnx/markingagentcalibration/2009/04/08\" " +
        "xmlns:dd=\"http://www.hp.com/schemas/imaging/con/dictionaries/1.0/\">Printing</cal:CalibrationState>";

    public static string BuildJobXml(string jobType) => string.Format(JobXml, System.Security.SecurityElement.Escape(jobType));

    // ---------------------------------------------------------------- naming the jobs

    static readonly Dictionary<string, (string Title, HpJobKind Kind, int Level)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cleaningPage"] = ("Level 1: light cleaning", HpJobKind.Clean, 1),
        ["cleaningPageLevel1"] = ("Level 2: medium cleaning", HpJobKind.Clean, 2),
        ["cleaningPageLevel2"] = ("Level 3: deep cleaning", HpJobKind.Clean, 3),
        ["cleaningVerificationPage"] = ("Cleaning check page", HpJobKind.Report, 0),
    };

    /// <summary>Only print-type jobs are offered: a printer's capability list is never taken as permission to run something that does not print a page.</summary>
    static bool LooksLikeAPage(string jobType) =>
        new[] { "page", "report", "diagnostic", "test", "demo", "sample" }.Any(w => jobType.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static string Humanize(string jobType)
    {
        var sb = new StringBuilder();
        foreach (var ch in jobType)
        {
            if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ');
            sb.Append(sb.Length == 0 ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public static List<HpJob> ParseJobs(string capXml)
    {
        var jobs = new List<HpJob>();
        XDocument doc;
        try { doc = XDocument.Parse(capXml); } catch { return jobs; }
        foreach (var name in doc.Descendants().Where(e => e.Name.LocalName == "JobType").Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct())
        {
            if (Known.TryGetValue(name, out var k)) jobs.Add(new HpJob(name, k.Title, k.Kind, k.Level));
            else if (name.Contains("clean", StringComparison.OrdinalIgnoreCase)) continue;      // an unknown cleaning variant: not offered
            else if (LooksLikeAPage(name)) jobs.Add(new HpJob(name, Humanize(name), HpJobKind.Report));
        }
        return jobs;
    }

    // ---------------------------------------------------------------- status

    static readonly Dictionary<string, string> StatusProblems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trayEmptyOrOpen"] = "The paper tray is empty or open. Load plain paper and close the tray.",
        ["jamInPrinter"] = "There is a paper jam. Clear it and try again.",
        ["closeDoorOrCover"] = "A door or cover is open. Close it and try again.",
        ["hardError"] = "The printer reports a hardware error. Switch it off and on, then try again.",
        ["outputBinFull"] = "The output tray is full. Empty it and try again.",
        ["unexpectedSizeInTray"] = "The paper in the tray is not the size the printer expects.",
        ["sizeMismatchInTray"] = "The paper in the tray is not the size the printer expects.",
        ["shuttingDown"] = "The printer is shutting down.",
        ["cancelJob"] = "The printer is cancelling a job. Try again in a moment.",
    };

    /// <summary>"ready", "processing" or another category from the printer's product status; null when it cannot be read.</summary>
    public static async Task<string?> GetStatusCategoryAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        try
        {
            var xml = await http.GetStringAsync(new Uri(baseUri, StatusPath), ct).ConfigureAwait(false);
            var cats = XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "StatusCategory").Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
            if (cats.Count == 0) return null;
            return cats.FirstOrDefault(c => StatusProblems.ContainsKey(c)) ?? (cats.Contains("processing") ? "processing" : cats[0]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { return null; }
    }

    public static string? ProblemFor(string? category) => category is not null && StatusProblems.TryGetValue(category, out var p) ? p : null;

    // ---------------------------------------------------------------- what the printer supports

    public static async Task<HpMaintenanceInfo> ProbeAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        var ledm = await ProbeLedmAsync(http, baseUri, ct).ConfigureAwait(false);
        if (ledm.Reachable) return ledm;
        var cdm = await HpCdm.ProbeAsync(http, baseUri, ct).ConfigureAwait(false);   // current models answer in JSON instead
        return cdm.Reachable ? cdm : ledm;
    }

    static async Task<HpMaintenanceInfo> ProbeLedmAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        string? capXml = null;
        try
        {
            using var r = await http.GetAsync(new Uri(baseUri, InternalPrintCapPath), ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode) capXml = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; }

        var status = await GetStatusCategoryAsync(http, baseUri, ct).ConfigureAwait(false);
        if (capXml is null && status is null)
            return new HpMaintenanceInfo { Problem = "The printer did not answer HP's web-services requests, so there is nothing to offer here." };

        var jobs = capXml is null ? new List<HpJob>() : ParseJobs(capXml);
        // an older firmware may not publish the capability list but still accept the basic cleaning
        if (capXml is null) jobs.Add(new HpJob("cleaningPage", Known["cleaningPage"].Title, HpJobKind.Clean, 1));

        return new HpMaintenanceInfo { Reachable = true, Jobs = jobs, AlignmentMode = await ProbeAlignmentAsync(http, baseUri, ct).ConfigureAwait(false) };
    }

    static async Task<string?> ProbeAlignmentAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        try
        {
            using var state = await http.GetAsync(new Uri(baseUri, CalibrationStatePath), ct).ConfigureAwait(false);
            if (!state.IsSuccessStatusCode) return null;                                                    // 404: no alignment support
            var xml = await http.GetStringAsync(new Uri(baseUri, ConsumablesPath), ct).ConfigureAwait(false);
            return ParseAlignmentMode(xml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; return null; }
    }

    public static string? ParseAlignmentMode(string consumablesXml)
    {
        try
        {
            var values = XDocument.Parse(consumablesXml).Descendants().Where(e => e.Name.LocalName == "AlignmentMode").Select(e => e.Value.Trim()).ToList();
            foreach (var wanted in new[] { "automatic", "semiAutomatic", "manual" })
                if (values.Any(v => v.Equals(wanted, StringComparison.OrdinalIgnoreCase))) return wanted;
        }
        catch (System.Xml.XmlException) { }
        return null;
    }

    // ---------------------------------------------------------------- doing things

    /// <summary>Send a maintenance print job and wait until the printer has finished it.</summary>
    public static async Task<string> RunJobAsync(HttpClient http, Uri baseUri, HpJob job, IProgress<string>? progress, CancellationToken ct,
        TimeSpan? maxWait = null, TimeSpan? poll = null, TimeSpan? startWindow = null)
    {
        if (job.Protocol == HpProtocol.Cdm) return await HpCdm.RunReportAsync(http, baseUri, job, progress, ct, maxWait, poll, startWindow).ConfigureAwait(false);
        var wait = maxWait ?? TimeSpan.FromMinutes(job.Kind == HpJobKind.Clean ? 12 : 4);
        var every = poll ?? TimeSpan.FromSeconds(2);
        var window = startWindow ?? TimeSpan.FromSeconds(20);

        var before = await GetStatusCategoryAsync(http, baseUri, ct).ConfigureAwait(false);
        if (ProblemFor(before) is { } problem) throw new InvalidOperationException(problem);
        if (before == "processing") throw new InvalidOperationException("The printer is busy with another job. Wait for it to finish and try again.");

        using (var content = new StringContent(BuildJobXml(job.JobType), Encoding.UTF8, "text/xml"))
        using (var resp = await http.PostAsync(new Uri(baseUri, InternalPrintPath), content, ct).ConfigureAwait(false))
        {
            Diag.Log($"HP: {job.JobType} -> HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"The printer refused “{job.Title}” (HTTP {(int)resp.StatusCode}). It may not support it, or it is busy.");
        }

        var started = DateTime.UtcNow;
        bool sawWork = false; int readyInARow = 0;
        while (DateTime.UtcNow - started < wait)
        {
            await Task.Delay(every, ct).ConfigureAwait(false);
            var cat = await GetStatusCategoryAsync(http, baseUri, ct).ConfigureAwait(false);
            Diag.Log($"HP: status {cat ?? "(no answer)"} after {(DateTime.UtcNow - started).TotalSeconds:0} s");
            if (ProblemFor(cat) is { } p) throw new InvalidOperationException(p);

            if (cat == "processing") { sawWork = true; readyInARow = 0; progress?.Report("The printer is working…"); continue; }
            if (cat == "ready")
            {
                readyInARow++;
                if (sawWork && readyInARow >= 2) return "finished";
                if (!sawWork && DateTime.UtcNow - started > window)
                {
                    // never started: a cleaning always takes a while, so the printer ignored it; a short report page may simply have been too quick to catch
                    if (job.Kind == HpJobKind.Clean)
                        throw new InvalidOperationException($"The printer did not react to “{job.Title}”: it stayed idle. Check that it has paper and no errors, or use HP Smart or the printer's own menu.");
                    return "sent";
                }
            }
        }
        throw new TimeoutException("The printer took too long to finish. Check it for errors.");
    }

    /// <summary>Starts a printhead alignment. Returns what the user has to do next. Manual alignment (choosing patterns) is not supported here.</summary>
    public static async Task<string> StartAlignmentAsync(HttpClient http, Uri baseUri, string mode, IProgress<string>? progress, CancellationToken ct,
        TimeSpan? maxWait = null, TimeSpan? poll = null, HpProtocol protocol = HpProtocol.Ledm)
    {
        // CDM printers print the page first (RunJobAsync with the "alignmentPage" report), then scan it: the caller drives the two steps
        if (protocol == HpProtocol.Cdm) return await HpCdm.ScanAlignmentAsync(http, baseUri, progress, ct, maxWait, poll).ConfigureAwait(false);
        if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return "This printer aligns by printing a page with numbered patterns and asking which line is best. HSA does not do that yet: use the printer web page (open it from the menu), HP Smart, or the printer's own menu.";

        var before = await GetStatusCategoryAsync(http, baseUri, ct).ConfigureAwait(false);
        if (ProblemFor(before) is { } problem) throw new InvalidOperationException(problem);

        string state = "";
        try { state = await http.GetStringAsync(new Uri(baseUri, CalibrationStatePath), ct).ConfigureAwait(false); } catch (HttpRequestException) { }
        if (state.Contains("ParmsRequested")) throw new InvalidOperationException("An earlier alignment is waiting for input. Switch the printer off and on, then start again.");
        if (state.Contains("Printing<")) throw new InvalidOperationException("An earlier alignment has not finished. Wait for the printer, or switch it off and on.");

        using (var content = new StringContent(CalibrationXml, Encoding.UTF8, "text/xml"))
        using (var resp = await http.PostAsync(new Uri(baseUri, CalibrationSessionPath), content, ct).ConfigureAwait(false))
        {
            Diag.Log($"HP: alignment start ({mode}) -> HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"The printer would not start the alignment (HTTP {(int)resp.StatusCode}).");
        }

        if (mode.Equals("semiAutomatic", StringComparison.OrdinalIgnoreCase))
            return "The alignment page is printing. When it is out, put it face down on the scanner glass and follow the prompt on the printer's screen or buttons.";

        // automatic: the printer prints and measures the page itself
        progress?.Report("Aligning…");
        var wait = maxWait ?? TimeSpan.FromMinutes(6);
        var every = poll ?? TimeSpan.FromSeconds(3);
        var started = DateTime.UtcNow; bool sawWork = false; int ready = 0;
        while (DateTime.UtcNow - started < wait)
        {
            await Task.Delay(every, ct).ConfigureAwait(false);
            var cat = await GetStatusCategoryAsync(http, baseUri, ct).ConfigureAwait(false);
            if (ProblemFor(cat) is { } p) throw new InvalidOperationException(p);
            if (cat == "processing") { sawWork = true; ready = 0; continue; }
            if (cat == "ready" && ++ready >= 2 && (sawWork || DateTime.UtcNow - started > TimeSpan.FromSeconds(20))) break;
        }
        return "The alignment finished. Print a test page to check the result.";
    }
}
