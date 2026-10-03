using System.Net;

namespace PrintHub.Core.Ipp;

public sealed class IppPrintOptions
{
    public int Copies { get; set; } = 1;
    /// <summary>one-sided, two-sided-long-edge, two-sided-short-edge</summary>
    public string Sides { get; set; } = "one-sided";
    public bool Color { get; set; } = true;
    public bool Landscape { get; set; }
    /// <summary>PWG media keyword, e.g. na_letter_8.5x11in or iso_a4_210x297mm. Null = printer default.</summary>
    public string? Media { get; set; }
    /// <summary>3 draft, 4 normal, 5 high. Null = default.</summary>
    public int? Quality { get; set; }
    public string? PageRanges { get; set; }
    /// <summary>print-scaling keyword: auto, auto-fit, fill, fit, none. Null = printer default.</summary>
    public string? PrintScaling { get; set; }
}

/// <summary>IPP client. Works with network printers and, through <c>UsbHttpProxy</c>, USB-attached ones.</summary>
public sealed class IppClient
{
    static readonly HttpClient SharedHttp = CreateHttp();
    readonly Uri _uri;
    readonly HttpClient _http;
    int _requestId = 1;

    public IppClient(Uri ippUri, HttpClient? http = null)
    {
        _uri = ippUri;
        _http = http ?? SharedHttp;
    }

    public Uri Uri => _uri;

    static HttpClient CreateHttp() => new(Http.LocalTls.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<IppMessage> SendAsync(IppMessage request, byte[]? document = null, CancellationToken ct = default)
    {
        request.RequestId = Interlocked.Increment(ref _requestId);
        var head = request.Encode();
        byte[] payload = head;
        if (document is { Length: > 0 })
        {
            payload = new byte[head.Length + document.Length];
            head.CopyTo(payload, 0);
            document.CopyTo(payload, head.Length);
        }

        var target = _uri.Scheme is "ipp" or "ipps" ? new UriBuilder(_uri) { Scheme = _uri.Scheme == "ipps" ? "https" : "http", Port = _uri.Port }.Uri : _uri;
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new("application/ipp");
        using var resp = await _http.PostAsync(target, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return IppMessage.Decode(bytes, out _);
    }

    static readonly string[] StatusAttributes =
    {
        "printer-make-and-model", "printer-info", "printer-location", "printer-uuid", "printer-firmware-string-version",
        "printer-device-id", "printer-state", "printer-state-message", "printer-state-reasons", "marker-names",
        "marker-levels", "marker-colors", "marker-types", "marker-low-levels", "marker-high-levels",
        "document-format-supported", "media-supported", "media-ready", "sides-supported", "print-color-mode-supported",
        "operations-supported", "pages-per-minute", "color-supported", "printer-more-info", "printer-uri-supported",
        "printer-supply-info-uri", "printer-is-accepting-jobs",
    };

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var req = IppMessage.CreateRequest(IppOp.GetPrinterAttributes, _uri);
        req.Add(IppTag.OperationAttributes, IppTag.Keyword, "requested-attributes", StatusAttributes.Cast<object>().ToArray());
        var resp = await SendAsync(req, null, ct).ConfigureAwait(false);
        if (!resp.Successful) throw new IppException(resp.Code, "Get-Printer-Attributes failed");
        return PrinterStatus.FromMessage(resp);
    }

    /// <summary>Print a document (PDF, JPEG, PWG/URF raster...). Returns the job id when reported.</summary>
    public async Task<int?> PrintAsync(byte[] data, string mimeType, string jobName, IppPrintOptions? o = null, CancellationToken ct = default)
    {
        o ??= new();
        var req = IppMessage.CreateRequest(IppOp.PrintJob, _uri);
        req.Add(IppTag.OperationAttributes, IppTag.Name, "job-name", jobName);
        req.Add(IppTag.OperationAttributes, IppTag.MimeMediaType, "document-format", mimeType);
        if (o.Copies > 1) req.Add(IppTag.JobAttributes, IppTag.Integer, "copies", o.Copies);
        req.Add(IppTag.JobAttributes, IppTag.Keyword, "sides", o.Sides);
        req.Add(IppTag.JobAttributes, IppTag.Keyword, "print-color-mode", o.Color ? "color" : "monochrome");
        if (o.Landscape) req.Add(IppTag.JobAttributes, IppTag.Enum, "orientation-requested", 4);
        if (o.Media is not null) req.Add(IppTag.JobAttributes, IppTag.Keyword, "media", o.Media);
        if (o.PrintScaling is not null) req.Add(IppTag.JobAttributes, IppTag.Keyword, "print-scaling", o.PrintScaling);
        if (o.Quality is { } q) req.Add(IppTag.JobAttributes, IppTag.Enum, "print-quality", q);
        if (o.PageRanges is not null && TryParseRanges(o.PageRanges, out var ranges))
            req.Add(IppTag.JobAttributes, IppTag.RangeOfInteger, "page-ranges", ranges.Cast<object>().ToArray());

        var resp = await SendAsync(req, data, ct).ConfigureAwait(false);
        if (!resp.Successful) throw new IppException(resp.Code, $"Print-Job rejected ({resp.GetString("status-message")})");
        return resp.Get("job-id")?.Values.OfType<int>().Cast<int?>().FirstOrDefault();
    }

    public async Task<List<IppJob>> GetJobsAsync(bool completed = false, CancellationToken ct = default)
    {
        var req = IppMessage.CreateRequest(IppOp.GetJobs, _uri);
        req.Add(IppTag.OperationAttributes, IppTag.Keyword, "which-jobs", completed ? "completed" : "not-completed");
        req.Add(IppTag.OperationAttributes, IppTag.Keyword, "requested-attributes", "job-id", "job-name", "job-state", "job-originating-user-name", "job-k-octets");
        var resp = await SendAsync(req, null, ct).ConfigureAwait(false);
        var jobs = new List<IppJob>();
        IppJob? cur = null;
        foreach (var a in resp.Attributes.Where(a => a.Group == IppTag.JobAttributes))
        {
            if (a.Name == "job-id") { cur = new IppJob { Id = (int)a.Values[0] }; jobs.Add(cur); }
            else if (cur is not null && a.Name == "job-name") cur.Name = a.Values[0].ToString() ?? "";
            else if (cur is not null && a.Name == "job-state") cur.State = (int)a.Values[0] switch { 3 => "Pending", 4 => "Held", 5 => "Processing", 6 => "Stopped", 7 => "Canceled", 8 => "Aborted", 9 => "Completed", _ => "Unknown" };
            else if (cur is not null && a.Name == "job-originating-user-name") cur.User = a.Values[0].ToString() ?? "";
        }
        return jobs;
    }

    public async Task CancelJobAsync(int jobId, CancellationToken ct = default)
    {
        var req = IppMessage.CreateRequest(IppOp.CancelJob, _uri);
        req.Add(IppTag.OperationAttributes, IppTag.Integer, "job-id", jobId);
        var resp = await SendAsync(req, null, ct).ConfigureAwait(false);
        if (!resp.Successful) throw new IppException(resp.Code, "Cancel-Job failed");
    }

    /// <summary>Ask the printer to flash/beep so the user can find it (Identify-Printer).</summary>
    public async Task IdentifyAsync(CancellationToken ct = default)
    {
        var req = IppMessage.CreateRequest(IppOp.IdentifyPrinter, _uri);
        req.Add(IppTag.OperationAttributes, IppTag.Keyword, "identify-actions", "flash", "sound");
        var resp = await SendAsync(req, null, ct).ConfigureAwait(false);
        if (!resp.Successful) throw new IppException(resp.Code, "Identify-Printer not supported by this printer");
    }

    static bool TryParseRanges(string text, out List<IppRange> ranges)
    {
        ranges = new();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-');
            if (!int.TryParse(bits[0], out var lo)) return false;
            int hi = lo;
            if (bits.Length == 2 && !int.TryParse(bits[1], out hi)) return false;
            ranges.Add(new IppRange(lo, hi));
        }
        return ranges.Count > 0;
    }
}

public sealed class IppJob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string User { get; set; } = "";
}

public sealed class IppException : Exception
{
    public ushort StatusCode { get; }
    public IppException(ushort status, string message) : base($"{message} (IPP status 0x{status:X4})") => StatusCode = status;
}
