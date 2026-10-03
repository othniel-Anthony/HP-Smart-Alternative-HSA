using System.Net;
using System.Net.Sockets;
using System.Text;
using PrintHub.Core.Http;

namespace PrintHub.Core.Usb;

/// <summary>
/// Exposes a printer's HTTP-over-USB channel (embedded web server, IPP, eSCL) as http://127.0.0.1:port/ so a browser,
/// WebView2 or any HTTP client can use a USB-attached printer exactly like a network one.
/// </summary>
public sealed class UsbHttpProxy : IAsyncDisposable
{
    readonly Func<Stream> _open;
    readonly SemaphoreSlim _usbGate = new(1, 1); // the USB interface carries one HTTP exchange at a time
    readonly CancellationTokenSource _cts = new();
    TcpListener? _listener;
    Stream? _usb;
    HttpReader? _usbReader;

    public UsbHttpProxy(UsbInterfaceInfo iface)
    {
        if (!iface.Openable) throw new InvalidOperationException($"Interface is not openable: {iface.Describe()}");
        var path = iface.DevicePath!;
        _open = () => { var s = UsbPipeStream.Open(path); s.Drain(); return s; };
    }

    /// <summary>Test hook: bridge to any byte stream that speaks HTTP (a fake device, a socket...).</summary>
    internal UsbHttpProxy(Func<Stream> openDeviceStream) => _open = openDeviceStream;

    public Uri BaseUri { get; private set; } = null!;
    public event Action<string>? Log;

    public void Start(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClient(client));
            }
        }
        catch { /* shutting down */ }
    }

    async Task HandleClient(TcpClient client)
    {
        using var _ = client;
        using var ns = client.GetStream();
        var reader = new HttpReader(ns);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var req = await reader.ReadAsync(isResponse: false, ct: _cts.Token);
                if (req is null) return;

                if ((req.GetHeader("Expect") ?? "").Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    // body was already read by HttpReader; just drop the header so the device doesn't see it
                    req.RemoveHeader("Expect");
                }
                bool closeAfter = (req.GetHeader("Connection") ?? "").Equals("close", StringComparison.OrdinalIgnoreCase);
                var resp = await ForwardAsync(req);
                var bytes = resp.Serialize();
                await ns.WriteAsync(bytes, _cts.Token);
                if (closeAfter) return;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    async Task<HttpMessage> ForwardAsync(HttpMessage req)
    {
        // Normalise: the device sees a plain Content-Length request addressed to "localhost".
        req.SetHeader("Host", "localhost");
        req.RemoveHeader("Proxy-Connection");
        req.RemoveHeader("Keep-Alive");
        req.RemoveHeader("Upgrade");
        if (req.Body.Length > 0 || req.Method is "POST" or "PUT") req.SetHeader("Content-Length", req.Body.Length.ToString());
        var wire = req.Serialize(alwaysContentLength: false);

        await _usbGate.WaitAsync(_cts.Token);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    EnsureOpen();
                    Log?.Invoke($"USB <- {req.StartLine} ({req.Body.Length} B)");
                    await _usb!.WriteAsync(wire, _cts.Token);
                    var resp = await _usbReader!.ReadAsync(isResponse: true, requestWasHead: req.Method == "HEAD", ct: _cts.Token)
                               ?? throw new IOException("Printer closed the USB connection");
                    Log?.Invoke($"USB -> {resp.StartLine} ({resp.Body.Length} B)");
                    Rewrite(resp);
                    return resp;
                }
                catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
                {
                    Log?.Invoke($"USB error ({ex.Message}); reopening interface");
                    CloseUsb();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return BadGateway($"USB transport error: {ex.Message}");
                }
            }
        }
        finally { _usbGate.Release(); }
    }

    void EnsureOpen()
    {
        if (_usb is not null) return;
        _usb = _open();
        _usbReader = new HttpReader(_usb);
    }

    void CloseUsb() { _usb?.Dispose(); _usb = null; _usbReader = null; }

    /// <summary>Make absolute redirects relative so they stay on the loopback proxy.</summary>
    static void Rewrite(HttpMessage resp)
    {
        var loc = resp.GetHeader("Location");
        if (loc is not null && Uri.TryCreate(loc, UriKind.Absolute, out var u)) resp.SetHeader("Location", u.PathAndQuery);
        resp.RemoveHeader("Connection");
    }

    static HttpMessage BadGateway(string text)
    {
        var m = new HttpMessage { StartLine = "HTTP/1.1 502 Bad Gateway", Body = Encoding.UTF8.GetBytes(text) };
        m.SetHeader("Content-Type", "text/plain; charset=utf-8");
        return m;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        await _usbGate.WaitAsync();
        CloseUsb();
        _usbGate.Release();
    }
}
