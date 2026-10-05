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
    /// <summary>False when the printer answered the web page request with "not found": it has no embedded web server (many entry-level inkjets).</summary>
    public bool HasWebPage { get; private set; } = true;

    /// <summary>Root of the printer's plain HTTP services (the loopback proxy for USB, the printer's address on the network). Used for HP web services.</summary>
    public Uri? HttpBase { get; private set; }
    bool? _ledmAvailable;
    DateTime _ledmCheckedAt;

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
            s.HttpBase = device.Address is { Length: > 0 } a ? new Uri($"http://{a}/") : device.WebUri is { } w ? new Uri(w.GetLeftPart(UriPartial.Authority) + "/") : null;
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
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(40));

                // 1. Is anything on this interface speaking HTTP at all? Ask for a page first, with a short patience, so an
                //    interface that only looks like HTTP costs seconds, not minutes. A request that times out leaves the USB
                //    connection waiting for the printer, so every probe gets its own fresh proxy.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool web = false;
                foreach (var probe in new[] { "", "DevMgmt/DiscoveryTree.xml" })
                {
                    var candidate = new UsbHttpProxy(iface);
                    try
                    {
                        candidate.Start();
                        using var quick = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        quick.CancelAfter(TimeSpan.FromSeconds(6));
                        using var r = await http.GetAsync(new Uri(candidate.BaseUri, probe), quick.Token);
                        web = probe.Length == 0 && ((int)r.StatusCode < 400 || r.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
                        proxy = candidate;
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Diag.Log($"USB probe '/{probe}' on {iface.Describe()}: {ex.GetType().Name} after {sw.ElapsedMilliseconds} ms");
                        await candidate.DisposeAsync();
                    }
                }
                if (proxy is null) { Error = $"The USB interface ({iface.Name}) did not answer HTTP requests."; continue; }

                // 2. What services does it offer?
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
                Diag.Log($"USB session on {iface.Describe()}: ipp={(ipp is not null)} escl={(escl is not null)} webpage={web} ({sw.ElapsedMilliseconds} ms)");

                _proxy = proxy; Ipp = ipp; Escl = escl; WebUri = proxy.BaseUri; HttpBase = proxy.BaseUri; HasWebPage = web; ViaUsb = true; Device.Usb = iface;
                Error = null;
                return;
            }
            catch (Exception ex) { Error = ex.Message; Diag.Log("USB session failed: " + ex.Message); if (proxy is not null) await proxy.DisposeAsync(); proxy = null; }
        }
        Error ??= "The USB interface did not answer HTTP requests.";
    }

    /// <summary>
    /// Ink levels from HP's web services, for HP printers whose IPP answer has none (or that have no IPP at all, like many USB-only inkjets).
    /// A "not available" answer is remembered for a while so the 20-second status refresh does not keep asking.
    /// </summary>
    public async Task<List<SupplyLevel>?> GetLedmSuppliesAsync(CancellationToken ct = default)
    {
        bool hp = Device.IsHp || Device.UsbCandidates.Any(u => u.VendorId == 0x03F0);
        if (!hp || HttpBase is null) return null;
        if (_ledmAvailable == false && DateTime.UtcNow - _ledmCheckedAt < TimeSpan.FromMinutes(10)) return null;
        try
        {
            using var http = ViaUsb ? new HttpClient() : new HttpClient(Http.LocalTls.CreateHandler());
            var list = await LedmClient.GetSuppliesAsync(HttpBase, http, ct).ConfigureAwait(false);
            _ledmAvailable = list is not null; _ledmCheckedAt = DateTime.UtcNow;
            if (list is not null) Diag.Log($"HP web services supplies: {string.Join(", ", list.Select(l => $"{l.Name} {l.Percent}%"))}");
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _ledmAvailable = false; _ledmCheckedAt = DateTime.UtcNow - TimeSpan.FromMinutes(9); // could not ask (printer asleep?): try again in a minute
            Diag.Log("HP web services supplies unavailable: " + ex.Message);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
    }
}
