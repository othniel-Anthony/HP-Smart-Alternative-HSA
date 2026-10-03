// A fake network MFP for end-to-end testing: IPP + eSCL + embedded web page on one port (loopback only).
//   FakePrinter [port=8631] [outDir]
// Everything it receives is written to <outDir>\events.log; print jobs are saved as job-<n>.<ext>.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PrintHub.Core.Http;
using PrintHub.Core.Ipp;

int port = args.Length > 0 ? int.Parse(args[0]) : 8631;
string outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "fake-printer");
Directory.CreateDirectory(outDir);
var eventsLog = Path.Combine(outDir, "events.log");
var gate = new object();
void Log(string s) { lock (gate) { File.AppendAllText(eventsLog, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}"); Console.WriteLine(s); } }

int nextJob = 100, nextScan = 1;
var scans = new Dictionary<int, ScanJob>();

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
if (args.Contains("mdns")) _ = Task.Run(MdnsResponder);   // advertise via Bonjour like a real printer
Console.WriteLine($"READY port={port} out={outDir}");
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Serve(client));
}

// ------------------------------------------------------------------------------------ mDNS
// Answers multicast-DNS queries for _ipp._tcp / _uscan._tcp with PTR + SRV + TXT + A records pointing at 127.0.0.1:<port>.
async Task MdnsResponder()
{
    try
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sock.Bind(new IPEndPoint(IPAddress.Any, 5353));
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || !nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse("224.0.0.251"), ua.Address)); } catch { }
        }
        Log("MDNS responder listening on 5353");
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            var r = await sock.ReceiveFromAsync(buf, SocketFlags.None, any);
            var text = Encoding.ASCII.GetString(buf, 0, r.ReceivedBytes);
            bool isQuery = r.ReceivedBytes > 12 && (buf[2] & 0x80) == 0;
            if (!isQuery || !(text.Contains("_ipp") || text.Contains("_uscan") || text.Contains("Fake mDNS"))) continue;
            Log($"MDNS query from {r.RemoteEndPoint}");
            await sock.SendToAsync(BuildMdnsAnswer(), SocketFlags.None, r.RemoteEndPoint);
        }
    }
    catch (Exception ex) { Log("MDNS responder failed: " + ex.Message); }
}

byte[] BuildMdnsAnswer()
{
    const string instance = "Fake mDNS OfficeJet";
    var ms = new MemoryStream();
    void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
    void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
    void Name(string n) { foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); ms.WriteByte((byte)b.Length); ms.Write(b); } ms.WriteByte(0); }
    void Record(string name, int type, byte[] data) { Name(name); U16(type); U16(0x8001); U32(120); U16(data.Length); ms.Write(data); }
    byte[] NameBytes(string n) { var m = new MemoryStream(); foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); m.WriteByte((byte)b.Length); m.Write(b); } m.WriteByte(0); return m.ToArray(); }
    byte[] Txt(params string[] kv) { var m = new MemoryStream(); foreach (var s in kv) { var b = Encoding.UTF8.GetBytes(s); m.WriteByte((byte)b.Length); m.Write(b); } return m.ToArray(); }
    byte[] Srv() { var m = new MemoryStream(); m.Write(new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }); m.Write(NameBytes("fakehost.local")); return m.ToArray(); }

    U16(0); U16(0x8400); U16(0); U16(8); U16(0); U16(0);
    foreach (var (svc, txt) in new[] {
        ("_ipp._tcp", Txt("rp=ipp/print", "ty=Fake mDNS OfficeJet 7777", "usb_MFG=HP", "usb_MDL=Fake mDNS OfficeJet 7777", "UUID=99999999-aaaa-bbbb-cccc-dddddddddddd", $"adminurl=http://127.0.0.1:{port}/")),
        ("_uscan._tcp", Txt("rs=eSCL", "ty=Fake mDNS OfficeJet 7777", "UUID=99999999-aaaa-bbbb-cccc-dddddddddddd")) })
    {
        var full = $"{instance}._{svc[1..]}.local";
        Record($"{svc}.local", 12, NameBytes(full));
        Record(full, 33, Srv());
        Record(full, 16, txt);
    }
    Record("fakehost.local", 1, new byte[] { 127, 0, 0, 1 });
    Record("fakehost.local", 1, new byte[] { 127, 0, 0, 1 });   // pad to the declared record count (duplicate A is harmless)
    return ms.ToArray();
}

async Task Serve(TcpClient c)
{
    using var _ = c;
    var ns = c.GetStream();
    var rd = new HttpReader(ns);
    try
    {
        while (await rd.ReadAsync(false) is { } req)
        {
            var path = req.StartLine.Split(' ')[1];
            var resp = Route(req, path);
            await ns.WriteAsync(resp.Serialize());
        }
    }
    catch { }
}

HttpMessage Reply(int code, string reason, string contentType, byte[] body, params (string, string)[] extra)
{
    var m = new HttpMessage { StartLine = $"HTTP/1.1 {code} {reason}", Body = body };
    if (contentType.Length > 0) m.SetHeader("Content-Type", contentType);
    foreach (var (k, v) in extra) m.SetHeader(k, v);
    return m;
}

HttpMessage Route(HttpMessage req, string path)
{
    Log($"HTTP {req.Method} {path} ua={req.GetHeader("User-Agent")?.Split(' ')[0]} len={req.Body.Length}");
    if (path.StartsWith("/ipp/print") && req.Method == "POST") return Ipp(req);
    if (path.StartsWith("/eSCL/")) return Escl(req, path);
    if (path == "/" || path.StartsWith("/#"))
        return Reply(200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<html><head><title>Fake Embedded Web Server</title></head><body style='font-family:Segoe UI'><h1>Fake HP OfficeJet EWS</h1><p id='ok'>Embedded web server is working.</p></body></html>"));
    return Reply(404, "Not Found", "text/plain", Array.Empty<byte>());
}

// ----------------------------------------------------------------------------------------- IPP
HttpMessage Ipp(HttpMessage req)
{
    var msg = IppMessage.Decode(req.Body, out int off);
    var resp = new IppMessage { Code = 0, RequestId = msg.RequestId };
    resp.Add(IppTag.OperationAttributes, IppTag.Charset, "attributes-charset", "utf-8");
    resp.Add(IppTag.OperationAttributes, IppTag.NaturalLanguage, "attributes-natural-language", "en");
    var P = IppTag.PrinterAttributes;

    switch (msg.Code)
    {
        case IppOp.GetPrinterAttributes:
            Log("IPP Get-Printer-Attributes");
            resp.Add(P, IppTag.Uri, "printer-uri-supported", $"ipp://127.0.0.1:{port}/ipp/print");
            resp.Add(P, IppTag.Name, "printer-name", "FAKE-OJ");
            resp.Add(P, IppTag.Text, "printer-make-and-model", "Fake HP OfficeJet Pro 9999");
            resp.Add(P, IppTag.Text, "printer-info", "Fake test printer");
            resp.Add(P, IppTag.Text, "printer-location", "Test bench");
            resp.Add(P, IppTag.Uri, "printer-uuid", "urn:uuid:11111111-2222-3333-4444-555555555555");
            resp.Add(P, IppTag.Text, "printer-firmware-string-version", "FAKE_2026.1");
            resp.Add(P, IppTag.Text, "printer-device-id", "MFG:HP;MDL:OfficeJet Pro 9999;SN:CNFAKE123;CMD:PDF,URF;");
            resp.Add(P, IppTag.Enum, "printer-state", 3);
            resp.Add(P, IppTag.Text, "printer-state-message", "");
            resp.Add(P, IppTag.Keyword, "printer-state-reasons", "marker-supply-low-warning");
            resp.Add(P, IppTag.Name, "marker-names", "Black Cartridge", "Cyan Cartridge", "Magenta Cartridge", "Yellow Cartridge");
            resp.Add(P, IppTag.Integer, "marker-levels", 72, 8, 55, 31);
            resp.Add(P, IppTag.Name, "marker-colors", "#000000", "#00AEEF", "#EC008C", "#FFF200");
            resp.Add(P, IppTag.Keyword, "marker-types", "ink-cartridge", "ink-cartridge", "ink-cartridge", "ink-cartridge");
            resp.Add(P, IppTag.Integer, "marker-low-levels", 10, 10, 10, 10);
            resp.Add(P, IppTag.Integer, "marker-high-levels", 100, 100, 100, 100);
            resp.Add(P, IppTag.MimeMediaType, "document-format-supported", "application/pdf", "image/jpeg", "image/urf");
            resp.Add(P, IppTag.Keyword, "media-supported", "na_letter_8.5x11in", "iso_a4_210x297mm", "na_index-4x6_4x6in");
            resp.Add(P, IppTag.Keyword, "media-ready", "na_letter_8.5x11in");
            resp.Add(P, IppTag.Keyword, "sides-supported", "one-sided", "two-sided-long-edge", "two-sided-short-edge");
            resp.Add(P, IppTag.Keyword, "print-color-mode-supported", "color", "monochrome");
            resp.Add(P, IppTag.Enum, "operations-supported", 2, 4, 8, 9, 10, 11, 60);
            resp.Add(P, IppTag.Integer, "pages-per-minute", 22);
            resp.Add(P, IppTag.Boolean, "color-supported", true);
            resp.Add(P, IppTag.Boolean, "printer-is-accepting-jobs", true);
            resp.Add(P, IppTag.Uri, "printer-more-info", $"http://127.0.0.1:{port}/");
            break;

        case IppOp.PrintJob:
        {
            int id = Interlocked.Increment(ref nextJob);
            var mime = msg.GetString("document-format") ?? "application/octet-stream";
            var ext = mime switch { "application/pdf" => "pdf", "image/jpeg" => "jpg", _ => "bin" };
            var doc = req.Body[off..];
            File.WriteAllBytes(Path.Combine(outDir, $"job-{id}.{ext}"), doc);
            string Attr(string n) => string.Join("+", msg.Get(n)?.Values.Select(v => v.ToString()) ?? Array.Empty<string>());
            Log($"IPP PRINT job={id} mime={mime} bytes={doc.Length} name={Attr("job-name")} copies={Attr("copies")} sides={Attr("sides")} color={Attr("print-color-mode")} media={Attr("media")} scaling={Attr("print-scaling")} quality={Attr("print-quality")} ranges={Attr("page-ranges")}");
            resp.Add(IppTag.JobAttributes, IppTag.Integer, "job-id", id);
            resp.Add(IppTag.JobAttributes, IppTag.Enum, "job-state", 9);
            break;
        }

        case IppOp.GetJobs:
            Log("IPP Get-Jobs");
            resp.Add(IppTag.JobAttributes, IppTag.Integer, "job-id", 9001);
            resp.Add(IppTag.JobAttributes, IppTag.Name, "job-name", "Held test job");
            resp.Add(IppTag.JobAttributes, IppTag.Enum, "job-state", 4);
            resp.Add(IppTag.JobAttributes, IppTag.Name, "job-originating-user-name", "tester");
            break;

        case IppOp.CancelJob:
            Log($"IPP Cancel-Job id={msg.GetInt("job-id")}");
            break;

        case IppOp.IdentifyPrinter:
            Log($"IPP Identify-Printer actions={string.Join("+", msg.GetStrings("identify-actions"))}");
            break;

        default:
            Log($"IPP unsupported op 0x{msg.Code:X4}");
            resp.Code = 0x0501;
            break;
    }
    return Reply(200, "OK", "application/ipp", resp.Encode());
}

// ---------------------------------------------------------------------------------------- eSCL
const string Ns = "xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\"";

string InputCaps(string tag) =>
    $"<scan:{tag}><scan:MinWidth>16</scan:MinWidth><scan:MaxWidth>2550</scan:MaxWidth><scan:MinHeight>16</scan:MinHeight><scan:MaxHeight>3507</scan:MaxHeight>" +
    "<scan:SettingProfiles><scan:SettingProfile><scan:ColorModes><scan:ColorMode>BlackAndWhite1</scan:ColorMode><scan:ColorMode>Grayscale8</scan:ColorMode><scan:ColorMode>RGB24</scan:ColorMode></scan:ColorModes>" +
    "<scan:DocumentFormats><pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat><pwg:DocumentFormat>application/pdf</pwg:DocumentFormat></scan:DocumentFormats>" +
    "<scan:SupportedResolutions><scan:DiscreteResolutions>" +
    string.Concat(new[] { 75, 150, 200, 300, 600 }.Select(r => $"<scan:DiscreteResolution><scan:XResolution>{r}</scan:XResolution><scan:YResolution>{r}</scan:YResolution></scan:DiscreteResolution>")) +
    $"</scan:DiscreteResolutions></scan:SupportedResolutions></scan:SettingProfile></scan:SettingProfiles></scan:{tag}>";

HttpMessage Escl(HttpMessage req, string path)
{
    var rel = path["/eSCL/".Length..];
    if (rel == "ScannerCapabilities")
    {
        var xml = $"<?xml version=\"1.0\"?><scan:ScannerCapabilities {Ns}><pwg:Version>2.6</pwg:Version><pwg:MakeAndModel>Fake HP OfficeJet Pro 9999</pwg:MakeAndModel>" +
                  $"<scan:Platen>{InputCaps("PlatenInputCaps")}</scan:Platen><scan:Adf>{InputCaps("AdfSimplexInputCaps")}{InputCaps("AdfDuplexInputCaps")}</scan:Adf></scan:ScannerCapabilities>";
        return Reply(200, "OK", "text/xml", Encoding.UTF8.GetBytes(xml));
    }
    if (rel == "ScannerStatus")
        return Reply(200, "OK", "text/xml", Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><scan:ScannerStatus {Ns}><pwg:Version>2.6</pwg:Version><pwg:State>Idle</pwg:State><scan:AdfState>ScannerAdfLoaded</scan:AdfState></scan:ScannerStatus>"));

    if (rel == "ScanJobs" && req.Method == "POST")
    {
        var xml = Encoding.UTF8.GetString(req.Body);
        string Tag(string t) => Regex.Match(xml, $"<(?:\\w+:)?{t}>([^<]*)<").Groups[1].Value;
        var job = new ScanJob
        {
            Id = nextScan++, Source = Tag("InputSource"), Color = Tag("ColorMode"), Dpi = int.Parse(Tag("XResolution")),
            Duplex = Tag("Duplex") == "true", Width = int.Parse(Tag("Width")), Height = int.Parse(Tag("Height")),
        };
        job.Pages = job.Source == "Platen" ? 1 : job.Duplex ? 6 : 3;
        lock (gate) scans[job.Id] = job;
        Log($"ESCL create job={job.Id} source={job.Source} color={job.Color} dpi={job.Dpi} duplex={job.Duplex} region={job.Width}x{job.Height} fmt={Tag("DocumentFormat")}");
        return Reply(201, "Created", "", Array.Empty<byte>(), ("Location", $"http://127.0.0.1:{port}/eSCL/ScanJobs/{job.Id}"));
    }

    var m = Regex.Match(rel, @"^ScanJobs/(\d+)(/NextDocument)?$");
    if (m.Success)
    {
        int id = int.Parse(m.Groups[1].Value);
        ScanJob? job; lock (gate) scans.TryGetValue(id, out job);
        if (job is null) return Reply(404, "Not Found", "", Array.Empty<byte>());
        if (req.Method == "DELETE") { Log($"ESCL delete job={id}"); return Reply(200, "OK", "", Array.Empty<byte>()); }
        if (m.Groups[2].Success)
        {
            if (!job.WarmedUp) { job.WarmedUp = true; Log($"ESCL job={id} 503 warming up"); return Reply(503, "Service Unavailable", "", Array.Empty<byte>()); }
            if (job.Delivered >= job.Pages) { Log($"ESCL job={id} 404 no more pages"); return Reply(404, "Not Found", "", Array.Empty<byte>()); }
            job.Delivered++;
            Log($"ESCL job={id} page {job.Delivered}/{job.Pages}");
            return Reply(200, "OK", "image/jpeg", MakePage(job, job.Delivered));
        }
    }
    return Reply(404, "Not Found", "", Array.Empty<byte>());
}

byte[] MakePage(ScanJob job, int n)
{
    int dpi = Math.Min(job.Dpi, 200);                       // keep it quick; the size still follows the request
    int w = Math.Max(64, job.Width * dpi / 300), h = Math.Max(64, job.Height * dpi / 300);
    using var bmp = new Bitmap(w, h);
    bmp.SetResolution(dpi, dpi);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        bool bed = job.Source == "Platen";
        g.Clear(bed ? Color.FromArgb(45, 45, 48) : Color.White);
        if (bed) { g.TranslateTransform(w / 2f, h / 2f); g.RotateTransform(2f); g.TranslateTransform(-w / 2f, -h / 2f); g.FillRectangle(Brushes.White, w * 0.08f, h * 0.06f, w * 0.84f, h * 0.88f); }
        float left = w * (bed ? 0.13f : 0.08f), top = h * (bed ? 0.10f : 0.06f);
        using var big = new Font("Arial", h / 28f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Arial", h / 50f, GraphicsUnit.Pixel);
        g.DrawString($"Fake scan page {n}", big, Brushes.Black, left, top);
        g.DrawString($"Source {job.Source} duplex {job.Duplex}", small, Brushes.Black, left, top + h / 14f);
        for (int i = 0; i < 12; i++) g.DrawString($"Invoice 2026-10-03 line {i + 1} total 123.45", small, Brushes.Black, left, top + h / 8f + i * (h / 40f));
        if (job.Color == "RGB24") g.FillEllipse(Brushes.Crimson, left, h * 0.62f, w * 0.12f, w * 0.12f);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Jpeg);
    return ms.ToArray();
}

sealed class ScanJob
{
    public int Id, Dpi, Width, Height, Pages, Delivered;
    public string Source = "", Color = "";
    public bool Duplex, WarmedUp;
}
