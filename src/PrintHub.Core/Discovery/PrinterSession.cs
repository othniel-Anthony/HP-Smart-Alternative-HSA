using PrintHub.Core.Escl;
using PrintHub.Core.Ipp;
using PrintHub.Core.Usb;

namespace PrintHub.Core.Discovery;

/// <summary>
/// A live connection to one printer. Network printers use their own addresses; USB printers get a loopback proxy,
/// so IPP, eSCL and the embedded web server behave identically for the rest of the app.
/// </summary>
public sealed class PrinterSession : IAsyncDisposable
{
    UsbHttpProxy? _proxy;

    public PrinterDevice Device { get; }
    public IppClient? Ipp { get; private set; }
    public EsclClient? Escl { get; private set; }
    public Uri? WebUri { get; private set; }
    public bool ViaUsb { get; private set; }
    public string? Error { get; private set; }

    PrinterSession(PrinterDevice d) => Device = d;

    public static async Task<PrinterSession> OpenAsync(PrinterDevice device, bool preferUsb = false, CancellationToken ct = default)
    {
        var s = new PrinterSession(device);
        bool useUsb = device.HasUsbHttp && (preferUsb || !device.HasNetwork);
        if (useUsb) await s.OpenUsbAsync(ct);
        if (!s.ViaUsb)
        {
            if (device.IppUri is not null) s.Ipp = new IppClient(device.IppUri);
            if (device.EsclUri is not null) s.Escl = new EsclClient(device.EsclUri);
            s.WebUri = device.WebUri;
        }
        return s;
    }

    async Task OpenUsbAsync(CancellationToken ct)
    {
        var candidates = Device.UsbCandidates.Where(u => u.Openable).ToList();
        if (Device.Usb is { Openable: true } u0 && !candidates.Contains(u0)) candidates.Insert(0, u0);
        if (candidates.Count == 0) { Error = "No USB web-services interface is bound to WinUSB."; return; }

        foreach (var iface in candidates)
        {
            UsbHttpProxy? proxy = null;
            try
            {
                proxy = new UsbHttpProxy(iface);
                proxy.Start();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

                IppClient? ipp = null; EsclClient? escl = null;
                foreach (var path in new[] { "ipp/print", "ipp/printer", "ipp" })
                {
                    try { var c = new IppClient(new Uri(proxy.BaseUri, path), http); await c.GetStatusAsync(cts.Token); ipp = new IppClient(new Uri(proxy.BaseUri, path)); break; } catch { }
                }
                try
                {
                    using var r = await http.GetAsync(new Uri(proxy.BaseUri, "eSCL/ScannerCapabilities"), cts.Token);
                    if (r.IsSuccessStatusCode) escl = new EsclClient(new Uri(proxy.BaseUri, "eSCL/"));
                }
                catch { }

                bool web = false;
                try { using var r = await http.GetAsync(proxy.BaseUri, cts.Token); web = (int)r.StatusCode < 500; } catch { }

                if (ipp is null && escl is null && !web) { await proxy.DisposeAsync(); continue; }
                _proxy = proxy; Ipp = ipp; Escl = escl; WebUri = proxy.BaseUri; ViaUsb = true; Device.Usb = iface;
                return;
            }
            catch (Exception ex) { Error = ex.Message; if (proxy is not null) await proxy.DisposeAsync(); }
        }
        Error ??= "The USB interface did not answer HTTP requests.";
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
    }
}
