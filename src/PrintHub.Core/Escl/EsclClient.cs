using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;

namespace PrintHub.Core.Escl;

public enum ScanSource { Flatbed, Feeder, FeederDuplex }
public enum ScanColor { Color, Grayscale, BlackAndWhite }

public sealed class EsclSourceCaps
{
    public int MaxWidth { get; set; } = 2550;   // 1/300 inch
    public int MaxHeight { get; set; } = 3507;
    public List<int> Resolutions { get; } = new();
    public List<string> ColorModes { get; } = new();
    public List<string> Formats { get; } = new();
}

public sealed class EsclCapabilities
{
    public string MakeAndModel { get; set; } = "";
    public string Version { get; set; } = "";
    public EsclSourceCaps? Flatbed { get; set; }
    public EsclSourceCaps? Feeder { get; set; }
    public bool FeederDuplex { get; set; }
    public EsclSourceCaps? Caps(ScanSource s) => s == ScanSource.Flatbed ? Flatbed : Feeder;
}

public sealed record EsclStatus(string State, string AdfState);

public sealed class EsclScanRequest
{
    public ScanSource Source { get; set; } = ScanSource.Flatbed;
    public ScanColor Color { get; set; } = ScanColor.Color;
    public int Dpi { get; set; } = 300;
    /// <summary>Scan area in 1/300 inch; 0 = full source size.</summary>
    public int WidthUnits { get; set; }
    public int HeightUnits { get; set; }
    public string Format { get; set; } = "image/jpeg";
}

public sealed class EsclException : Exception
{
    public EsclException(string message) : base(message) { }
}

/// <summary>eSCL (AirScan / Mopria Scan) client: HTTP based scanning supported by most current HP, Epson, Canon, Brother devices.</summary>
public sealed class EsclClient
{
    static readonly XNamespace Scan = "http://schemas.hp.com/imaging/escl/2011/05/03";
    static readonly XNamespace Pwg = "http://www.pwg.org/schemas/2010/12/sm";

    readonly HttpClient _http;
    readonly Uri _root;

    public EsclClient(Uri root, HttpClient? http = null)
    {
        _root = root.AbsoluteUri.EndsWith('/') ? root : new Uri(root.AbsoluteUri + "/");
        _http = http ?? new HttpClient(Http.LocalTls.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Uri Root => _root;

    public async Task<EsclCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var xml = await _http.GetStringAsync(new Uri(_root, "ScannerCapabilities"), cts.Token).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var caps = new EsclCapabilities
        {
            MakeAndModel = First(doc, "MakeAndModel") ?? "",
            Version = First(doc, "Version") ?? "",
        };
        caps.Flatbed = ParseSource(doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PlatenInputCaps"));
        var adfSimplex = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AdfSimplexInputCaps");
        caps.Feeder = ParseSource(adfSimplex);
        caps.FeederDuplex = doc.Descendants().Any(e => e.Name.LocalName == "AdfDuplexInputCaps");
        return caps;
    }

    static string? First(XDocument d, string local) => d.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim();

    static EsclSourceCaps? ParseSource(XElement? e)
    {
        if (e is null) return null;
        var c = new EsclSourceCaps();
        if (int.TryParse(Child(e, "MaxWidth"), out var w)) c.MaxWidth = w;
        if (int.TryParse(Child(e, "MaxHeight"), out var h)) c.MaxHeight = h;
        foreach (var cm in e.Descendants().Where(x => x.Name.LocalName == "ColorMode")) if (!c.ColorModes.Contains(cm.Value.Trim())) c.ColorModes.Add(cm.Value.Trim());
        foreach (var f in e.Descendants().Where(x => x.Name.LocalName is "DocumentFormat" or "DocumentFormatExt")) if (!c.Formats.Contains(f.Value.Trim())) c.Formats.Add(f.Value.Trim());
        foreach (var r in e.Descendants().Where(x => x.Name.LocalName == "DiscreteResolution"))
            if (int.TryParse(Child(r, "XResolution"), out var x) && !c.Resolutions.Contains(x)) c.Resolutions.Add(x);
        var range = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "ResolutionRange");
        if (range is not null && c.Resolutions.Count == 0)
        {
            var xr = range.Descendants().FirstOrDefault(x => x.Name.LocalName == "XResolutionRange");
            if (xr is not null && int.TryParse(Child(xr, "Min"), out var min) && int.TryParse(Child(xr, "Max"), out var max))
                c.Resolutions.AddRange(new[] { 75, 100, 150, 200, 300, 600, 1200, 2400 }.Where(r => r >= min && r <= max));
        }
        c.Resolutions.Sort();
        return c;
    }

    static string? Child(XElement e, string local) => e.Descendants().FirstOrDefault(x => x.Name.LocalName == local)?.Value.Trim();

    public async Task<EsclStatus> GetStatusAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var doc = XDocument.Parse(await _http.GetStringAsync(new Uri(_root, "ScannerStatus"), cts.Token).ConfigureAwait(false));
        return new EsclStatus(First(doc, "State") ?? "Unknown", First(doc, "AdfState") ?? "");
    }

    public static string BuildSettings(EsclScanRequest r, EsclSourceCaps? caps)
    {
        int w = r.WidthUnits > 0 ? r.WidthUnits : caps?.MaxWidth ?? 2550;
        int h = r.HeightUnits > 0 ? r.HeightUnits : caps?.MaxHeight ?? 3507;
        w = Math.Min(w, caps?.MaxWidth ?? w); h = Math.Min(h, caps?.MaxHeight ?? h);
        string color = r.Color == ScanColor.Color ? "RGB24" : "Grayscale8";
        string source = r.Source == ScanSource.Flatbed ? "Platen" : "Feeder";

        var doc = new XDocument(
            new XElement(Scan + "ScanSettings",
                new XAttribute(XNamespace.Xmlns + "scan", Scan.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "pwg", Pwg.NamespaceName),
                new XElement(Pwg + "Version", "2.0"),
                new XElement(Pwg + "ScanRegions",
                    new XElement(Pwg + "ScanRegion",
                        new XElement(Pwg + "ContentRegionUnits", "escl:ThreeHundredthsOfInches"),
                        new XElement(Pwg + "Height", h),
                        new XElement(Pwg + "Width", w),
                        new XElement(Pwg + "XOffset", 0),
                        new XElement(Pwg + "YOffset", 0))),
                new XElement(Pwg + "InputSource", source),
                new XElement(Scan + "ColorMode", color),
                new XElement(Scan + "XResolution", r.Dpi),
                new XElement(Scan + "YResolution", r.Dpi),
                new XElement(Pwg + "DocumentFormat", r.Format),
                r.Source == ScanSource.FeederDuplex ? new XElement(Scan + "Duplex", "true") : null));
        return doc.Declaration is null ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + doc.ToString(SaveOptions.DisableFormatting) : doc.ToString();
    }

    /// <summary>Scan and yield each page as encoded image bytes (JPEG by default). Flatbed yields one page; the feeder yields until empty.</summary>
    public async IAsyncEnumerable<byte[]> ScanAsync(EsclScanRequest request, EsclSourceCaps? caps = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildSettings(request, caps);
        Uri? job = null;
        for (int attempt = 0; attempt < 30 && job is null; attempt++)
        {
            using var content = new StringContent(body, Encoding.UTF8, "text/xml");
            using var resp = await _http.PostAsync(new Uri(_root, "ScanJobs"), content, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) { await Task.Delay(1000, ct); continue; } // scanner busy / warming up
            if (resp.StatusCode != HttpStatusCode.Created && !resp.IsSuccessStatusCode)
                throw new EsclException($"Scanner rejected the job: {(int)resp.StatusCode} {resp.ReasonPhrase}. {await SafeText(resp)}");
            var loc = resp.Headers.Location ?? throw new EsclException("Scanner did not return a job location.");
            job = loc.IsAbsoluteUri ? new Uri(new Uri(_root, "/"), loc.PathAndQuery) : new Uri(_root, loc);
            // Printers sometimes answer with their internal host name; keep our own authority so USB proxy / NAT still work.
            if (job.Authority != _root.Authority) job = new UriBuilder(job) { Host = _root.Host, Port = _root.Port, Scheme = _root.Scheme }.Uri;
        }
        if (job is null) throw new EsclException("The scanner stayed busy. Try again in a moment.");

        int pages = 0;
        try
        {
            while (true)
            {
                byte[]? page = null;
                for (int wait = 0; wait < 120; wait++)
                {
                    using var r = await _http.GetAsync(new Uri(job.AbsoluteUri.TrimEnd('/') + "/NextDocument"), ct).ConfigureAwait(false);
                    if (r.StatusCode == HttpStatusCode.OK) { page = await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false); break; }
                    if (r.StatusCode == HttpStatusCode.NotFound || r.StatusCode == HttpStatusCode.Gone) break;
                    if (r.StatusCode == HttpStatusCode.ServiceUnavailable || r.StatusCode == HttpStatusCode.Conflict) { await Task.Delay(1000, ct); continue; }
                    throw new EsclException($"Scanner error while reading page: {(int)r.StatusCode} {r.ReasonPhrase}");
                }
                if (page is null) break;
                pages++;
                yield return page;
                if (request.Source == ScanSource.Flatbed) break;
            }
        }
        finally
        {
            try { using var del = new HttpRequestMessage(HttpMethod.Delete, job); using var _ = await _http.SendAsync(del, CancellationToken.None); } catch { }
        }
        if (pages == 0) throw new EsclException(request.Source == ScanSource.Flatbed ? "The scanner returned no image." : "No pages were found in the document feeder.");
    }

    static async Task<string> SafeText(HttpResponseMessage r) { try { return await r.Content.ReadAsStringAsync(); } catch { return ""; } }
}
