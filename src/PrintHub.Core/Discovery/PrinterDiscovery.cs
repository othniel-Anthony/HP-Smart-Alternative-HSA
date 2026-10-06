using System.Net;
using PrintHub.Core.Printing;
using PrintHub.Core.Usb;

namespace PrintHub.Core.Discovery;

/// <summary>Finds printers from every source and merges duplicates into one <see cref="PrinterDevice"/> per physical printer.</summary>
public static class PrinterDiscovery
{
    /// <summary>Optional hook so the app layer can add WIA scanners (requires COM, kept out of this file).</summary>
    public static Func<List<(string Id, string Name)>>? WiaProvider { get; set; } = Scanning.WiaScanner.ListScanners;

    /// <summary>Optional: receives timing lines such as "WIA scanners took 2300 ms".</summary>
    public static Action<string>? Trace { get; set; }

    static T Timed<T>(string what, Func<T> f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return f(); } finally { Trace?.Invoke($"{what} took {sw.ElapsedMilliseconds} ms"); }
    }

    public static async Task<List<PrinterDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var mdnsTask = Task.Run(async () => { var sw = System.Diagnostics.Stopwatch.StartNew(); try { return await MdnsClient.BrowseAsync(ct: ct); } finally { Trace?.Invoke($"Bonjour search took {sw.ElapsedMilliseconds} ms"); } }, ct);
        var local = await Task.Run(() => (
            Spooler: Timed("Windows printer list", () => SafeList(SpoolerPrinters.List)),
            Usb: Timed("USB scan", () => SafeList(() => UsbDeviceScanner.FindHttpInterfaces(true).ToList())),
            Wia: Timed("WIA scanner list", () => SafeList(() => WiaProvider?.Invoke() ?? new()))), ct);

        List<MdnsService> services;
        try { services = await mdnsTask; } catch { services = new(); }

        var devices = new List<PrinterDevice>();

        // 1. Network printers via mDNS, grouped by address
        foreach (var grp in services.GroupBy(s => s.Addresses.FirstOrDefault()?.ToString() ?? s.Host))
        {
            var d = new PrinterDevice { Address = grp.First().Addresses.FirstOrDefault()?.ToString() };
            string host = d.Address ?? grp.First().Host;
            foreach (var s in grp)
            {
                var txt = s.Txt;
                string ty = txt.GetValueOrDefault("ty") ?? txt.GetValueOrDefault("product")?.Trim('(', ')') ?? s.Instance;
                if (d.Name == "" || ty.Length > d.Name.Length) d.Name = ty;
                d.Manufacturer = d.Manufacturer != "" ? d.Manufacturer : txt.GetValueOrDefault("usb_MFG") ?? ty.Split(' ')[0];
                d.Model = d.Model != "" ? d.Model : txt.GetValueOrDefault("usb_MDL") ?? ty;
                if (txt.GetValueOrDefault("UUID") is { } uuid) d.Id = uuid;

                switch (s.Type)
                {
                    case "_ipp._tcp" or "_ipps._tcp":
                        if (d.IppUri is null || s.Type == "_ipp._tcp")
                        {
                            var rp = (txt.GetValueOrDefault("rp") ?? "ipp/print").TrimStart('/');
                            d.IppUri = new Uri($"{(s.Type == "_ipps._tcp" ? "https" : "http")}://{host}:{s.Port}/{rp}");
                        }
                        if (txt.GetValueOrDefault("adminurl") is { Length: > 0 } admin && Uri.TryCreate(admin, UriKind.Absolute, out var au)) d.WebUri = au;
                        break;
                    case "_uscan._tcp" or "_uscans._tcp":
                        if (d.EsclUri is null || s.Type == "_uscan._tcp")
                        {
                            var rs = (txt.GetValueOrDefault("rs") ?? "eSCL").Trim('/');
                            d.EsclUri = new Uri($"{(s.Type == "_uscans._tcp" ? "https" : "http")}://{host}:{s.Port}/{rs}/");
                        }
                        break;
                }
            }
            if (d.IppUri is null && d.EsclUri is null) continue; // raw-socket-only entries are reachable through the Windows queue
            d.WebUri ??= new Uri($"http://{host}/");
            devices.Add(d);
        }

        // 2. Windows print queues
        foreach (var q in local.Spooler)
        {
            if (q.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && q.Port.StartsWith("PORTPROMPT")) continue; // Print to PDF / XPS
            if (q.Name.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase) || q.Name.Contains("Fax", StringComparison.OrdinalIgnoreCase) && q.Port.StartsWith("SHRFAX")) continue;
            var ip = SpoolerPrinters.AddressFromPort(q.Port);
            var existing = devices.FirstOrDefault(d => (ip is not null && d.Address == ip) || PrinterDevice.Similar(d.Name, q.Name) || PrinterDevice.Similar(d.Name, q.Driver));
            var rec = new PrinterDevice { Name = q.Name, Manufacturer = q.Driver.Split(' ')[0], Model = q.Name, Address = ip, SpoolerName = q.Name, SpoolerPort = q.Port, SpoolerDriver = q.Driver, SpoolerStatus = q.Status, SpoolerOffline = q.IsOffline };
            if (existing is not null) existing.Merge(rec); else devices.Add(rec);
        }

        // 3. USB HTTP interfaces (embedded web server over USB)
        foreach (var grp in local.Usb.GroupBy(u => (u.VendorId, u.ProductId, u.ContainerId)))
        {
            var first = grp.OrderByDescending(u => u.Openable).First();
            var rec = new PrinterDevice
            {
                Name = first.Name, Manufacturer = first.VendorId == 0x03F0 ? "HP" : "", Model = first.Name,
                Usb = grp.FirstOrDefault(u => u.Openable), UsbCandidates = grp.ToList(),
            };
            var existing = devices.FirstOrDefault(d => PrinterDevice.Similar(d.Name, first.Name) || PrinterDevice.Similar(d.Model, first.Name));
            if (existing is not null) existing.Merge(rec); else devices.Add(rec);
        }

        // 4. WIA scanners
        foreach (var w in local.Wia)
        {
            var existing = devices.FirstOrDefault(d => PrinterDevice.Similar(d.Name, w.Name));
            if (existing is not null) existing.WiaDeviceId ??= w.Id;
            else devices.Add(new PrinterDevice { Name = w.Name, Model = w.Name, WiaDeviceId = w.Id });
        }

        foreach (var d in devices) if (d.Manufacturer == "") d.Manufacturer = d.Name.Split(' ')[0];
        Timed("USB presence", () => { PrinterPicker.MarkUsb(devices); return 0; });
        return devices.OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Probe a printer by IP or host name when discovery cannot see it (other subnet, mDNS blocked...).
    /// Accepts "192.168.1.50", "printer.local" or "192.168.1.50:8080" (a specific port for IPP / eSCL / web).
    /// All candidate URLs are tried at once, so a wrong address fails after one timeout instead of nine.
    /// </summary>
    public static async Task<PrinterDevice?> ProbeAddressAsync(string input, CancellationToken ct = default)
    {
        input = input.Trim().Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("https://", "", StringComparison.OrdinalIgnoreCase).Trim('/');
        string host = input; int? port = null;
        int colon = input.LastIndexOf(':');
        if (colon > 0 && input.IndexOf(':') == colon && int.TryParse(input[(colon + 1)..], out var p) && p is > 0 and < 65536) { host = input[..colon]; port = p; }
        if (host.Length == 0) return null;

        string hp = port is null ? host : $"{host}:{port}";
        string[] ippCandidates = port is null
            ? new[] { $"http://{host}:631/ipp/print", $"http://{host}/ipp/print", $"https://{host}/ipp/print", $"http://{host}:631/ipp/printer", $"http://{host}:631/" }
            : new[] { $"http://{hp}/ipp/print", $"https://{hp}/ipp/print", $"http://{hp}/ipp/printer", $"http://{hp}/" };
        string[] esclCandidates = port is null
            ? new[] { $"http://{host}/eSCL/", $"https://{host}/eSCL/", $"http://{host}:8080/eSCL/" }
            : new[] { $"http://{hp}/eSCL/", $"https://{hp}/eSCL/" };

        using var http = new HttpClient(Http.LocalTls.CreateHandler()) { Timeout = TimeSpan.FromSeconds(4) };

        async Task<(Uri Uri, Ipp.PrinterStatus Status)?> TryIpp(string url)
        {
            try { var st = await new Ipp.IppClient(new Uri(url), http).GetStatusAsync(ct); return (new Uri(url), st); } catch { return null; }
        }
        async Task<Uri?> TryEscl(string url)
        {
            try { using var r = await http.GetAsync(url + "ScannerCapabilities", ct); return r.IsSuccessStatusCode ? new Uri(url) : null; } catch { return null; }
        }

        var ippTasks = ippCandidates.Select(TryIpp).ToList();
        var esclTasks = esclCandidates.Select(TryEscl).ToList();
        await Task.WhenAll(ippTasks.Concat<Task>(esclTasks));

        // keep the order of the candidate lists (most standard first) among the ones that answered
        var ipp = ippTasks.Select(t => t.Result).FirstOrDefault(r => r is not null);
        var escl = esclTasks.Select(t => t.Result).FirstOrDefault(r => r is not null);
        if (ipp is null && escl is null) return null;

        var d = new PrinterDevice { Address = hp, Id = hp, EsclUri = escl };
        if (ipp is { } found)
        {
            var st = found.Status;
            d.IppUri = found.Uri;
            d.Name = st.MakeAndModel.Length > 0 ? st.MakeAndModel : hp;
            d.Model = d.Name;
            d.Manufacturer = st.DeviceId.GetValueOrDefault("MFG") ?? d.Name.Split(' ')[0];
            if (st.MoreInfoUri is { } mi && Uri.TryCreate(mi, UriKind.Absolute, out var u)) d.WebUri = u;
        }
        if (d.Name == "") { d.Name = hp; d.Model = hp; }
        if (d.Manufacturer == "") d.Manufacturer = d.Name.Split(' ')[0];
        d.WebUri ??= new Uri($"http://{hp}/");
        return d;
    }
    static List<T> SafeList<T>(Func<List<T>> f) { try { return f(); } catch { return new(); } }
}
