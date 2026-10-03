using System.Net;
using System.Net.Sockets;
using System.Text;
using PrintHub.Core.Escl;
using PrintHub.Core.Http;
using PrintHub.Core.Ipp;
using PrintHub.Core.Usb;
using Xunit;

namespace PrintHub.Tests;

/// <summary>A TCP server standing in for a printer on the other end of a USB cable.</summary>
sealed class FakeUsbPrinter : IAsyncDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public List<HttpMessage> Requests { get; } = new();
    public int Connections;
    bool _droppedOnce;

    public FakeUsbPrinter() { _l.Start(); _ = Task.Run(Accept); }

    async Task Accept()
    {
        try
        {
            while (true)
            {
                var c = await _l.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref Connections);
                _ = Task.Run(() => Serve(c));
            }
        }
        catch { }
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
                lock (Requests) Requests.Add(req);
                var path = req.StartLine.Split(' ')[1];
                if (path == "/drop" && !_droppedOnce) { _droppedOnce = true; return; } // device resets the connection mid-request
                await ns.WriteAsync(Reply(path));
            }
        }
        catch { }
    }

    static string Chunked(string contentType, byte[] body)
    {
        var sb = new StringBuilder($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nTransfer-Encoding: chunked\r\n\r\n");
        for (int i = 0; i < body.Length; i += 7)
        {
            var n = Math.Min(7, body.Length - i);
            sb.Append(n.ToString("X")).Append("\r\n").Append(Encoding.Latin1.GetString(body, i, n)).Append("\r\n");
        }
        return sb.Append("0\r\n\r\n").ToString();
    }

    const string Caps =
        "<scan:ScannerCapabilities xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\">" +
        "<pwg:Version>2.6</pwg:Version><pwg:MakeAndModel>Fake USB MFP</pwg:MakeAndModel><scan:Platen><scan:PlatenInputCaps>" +
        "<scan:MaxWidth>2550</scan:MaxWidth><scan:MaxHeight>3507</scan:MaxHeight><scan:SettingProfiles><scan:SettingProfile>" +
        "<scan:ColorModes><scan:ColorMode>RGB24</scan:ColorMode></scan:ColorModes><scan:SupportedResolutions><scan:DiscreteResolutions>" +
        "<scan:DiscreteResolution><scan:XResolution>300</scan:XResolution><scan:YResolution>300</scan:YResolution></scan:DiscreteResolution>" +
        "</scan:DiscreteResolutions></scan:SupportedResolutions></scan:SettingProfile></scan:SettingProfiles></scan:PlatenInputCaps></scan:Platen></scan:ScannerCapabilities>";

    static byte[] Reply(string path)
    {
        if (path == "/") return Encoding.Latin1.GetBytes(Chunked("text/html", Encoding.UTF8.GetBytes("<html><body>Embedded Web Server</body></html>")));
        if (path == "/redirect") return Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://printer.local:80/final?x=1\r\nContent-Length: 0\r\n\r\n");
        if (path == "/final?x=1") return Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\ndone");
        if (path == "/drop") return Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\nrecovered");
        if (path.StartsWith("/eSCL/ScannerCapabilities")) return Encoding.Latin1.GetBytes(Chunked("text/xml", Encoding.UTF8.GetBytes(Caps)));
        if (path.StartsWith("/ipp/print"))
        {
            var resp = new IppMessage { Code = 0 };
            resp.Add(IppTag.PrinterAttributes, IppTag.Name, "printer-info", "Fake USB MFP");
            resp.Add(IppTag.PrinterAttributes, IppTag.Enum, "printer-state", 3);
            resp.Add(IppTag.PrinterAttributes, IppTag.Keyword, "marker-names", "Black", "Cyan");
            resp.Add(IppTag.PrinterAttributes, IppTag.Integer, "marker-levels", 64, 8);
            return Encoding.Latin1.GetBytes(Chunked("application/ipp", resp.Encode()));
        }
        return Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); return ValueTask.CompletedTask; }
}

public class UsbProxyTests
{
    static UsbHttpProxy Bridge(FakeUsbPrinter dev)
    {
        var p = new UsbHttpProxy(() => new TcpClient("127.0.0.1", dev.Port).GetStream());
        p.Start();
        return p;
    }

    [Fact]
    public async Task Embedded_web_server_page_is_served_through_the_proxy()
    {
        await using var dev = new FakeUsbPrinter();
        await using var proxy = Bridge(dev);
        using var http = new HttpClient();
        var html = await http.GetStringAsync(proxy.BaseUri);
        Assert.Contains("Embedded Web Server", html);
        Assert.Equal("localhost", dev.Requests[0].GetHeader("Host")); // normalised for the device
    }

    [Fact]
    public async Task Redirects_stay_on_the_loopback_proxy()
    {
        await using var dev = new FakeUsbPrinter();
        await using var proxy = Bridge(dev);
        using var http = new HttpClient();
        Assert.Equal("done", await http.GetStringAsync(new Uri(proxy.BaseUri, "redirect")));
    }

    [Fact]
    public async Task Ipp_status_and_supplies_work_over_the_usb_channel()
    {
        await using var dev = new FakeUsbPrinter();
        await using var proxy = Bridge(dev);
        var st = await new IppClient(new Uri(proxy.BaseUri, "ipp/print")).GetStatusAsync();
        Assert.Equal(PrinterState.Idle, st.State);
        Assert.Equal(2, st.Supplies.Count);
        Assert.Equal(64, st.Supplies[0].Percent);
        Assert.True(st.Supplies[1].IsLow);
        var last = dev.Requests.Last();
        Assert.Equal("application/ipp", last.GetHeader("Content-Type"));
        Assert.Null(last.GetHeader("Transfer-Encoding")); // the device never sees chunked request bodies
        Assert.NotNull(last.GetHeader("Content-Length"));
    }

    [Fact]
    public async Task Escl_capabilities_work_over_the_usb_channel()
    {
        await using var dev = new FakeUsbPrinter();
        await using var proxy = Bridge(dev);
        var caps = await new EsclClient(new Uri(proxy.BaseUri, "eSCL/")).GetCapabilitiesAsync();
        Assert.Equal("Fake USB MFP", caps.MakeAndModel);
        Assert.Equal(3507, caps.Flatbed!.MaxHeight);
        Assert.Equal(new[] { 300 }, caps.Flatbed.Resolutions);
    }

    [Fact]
    public async Task Proxy_reopens_the_device_after_the_usb_connection_drops()
    {
        await using var dev = new FakeUsbPrinter();
        await using var proxy = Bridge(dev);
        using var http = new HttpClient();
        Assert.Contains("Embedded", await http.GetStringAsync(proxy.BaseUri));
        Assert.Equal("recovered", await http.GetStringAsync(new Uri(proxy.BaseUri, "drop")));
        Assert.True(dev.Connections >= 2);
    }

    [Fact]
    public async Task Concurrent_requests_are_serialised_on_the_single_usb_pipe()
    {
        await using var dev = new FakeUsbPrinter();
        await using var proxy = Bridge(dev);
        using var http = new HttpClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => http.GetStringAsync(proxy.BaseUri)));
        Assert.All(results, r => Assert.Contains("Embedded Web Server", r));
        Assert.Equal(1, dev.Connections); // one USB connection reused for everything
    }
}
