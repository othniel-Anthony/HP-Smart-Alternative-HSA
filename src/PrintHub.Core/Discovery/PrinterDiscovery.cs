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

    /// <summary>Names that describe a driver or a function of the printer rather than its model.</summary>
    internal static bool IsGenericInterfaceName(string name) =>
        new[] { "universal printing", "(rest)", "(mtp", "composite", "usb printing support", "scanner", "utility", "ledm", "unusedscanstub" }.Any(g => name.Contains(g, StringComparison.OrdinalIgnoreCase));

    /// <summary>"HP ColorLaserJet MFP M282-M285(REST)" is the REST function of the "HP ColorLaserJet MFP M282-M285": drop the function label.</summary>
    internal static string CleanInterfaceName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\s*\((?:REST|IPP WinUSB|MTP NULL|MTP|USB|SOAP Fax|LEDM)\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    /// <summary>The Windows queue of a printer's fax function ("Fax - HP ColorLaserJet MFP M282-M285").</summary>
    internal static bool IsFaxQueue(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^fax\b|\bfax\)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A cheap summary (a few milliseconds) of what is installed in Windows and plugged in by USB right now. When it changes, a printer was added, removed,
    /// plugged in, switched on or off, and HSA searches again by itself. Job counts and other things that change while printing are left out.
    /// </summary>
    public static string Fingerprint()
    {
        var parts = new List<string>();
        try { parts.AddRange(SpoolerPrinters.List().Select(q => $"queue|{q.Name}|{q.Port}|{q.IsOffline}")); } catch { }
        try { parts.AddRange(Printing.EpsonMaintenance.PresentPrintInterfaces().Select(p => $"print|{p.Path}")); } catch { }
        try { parts.AddRange(UsbDeviceScanner.FindHttpInterfaces(true).Select(u => $"web|{u.InstanceId}|{u.Service}")); } catch { }
        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    static T Timed<T>(string what, Func<T> f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return f(); } finally { Trace?.Invoke($"{what} took {sw.ElapsedMilliseconds} ms"); }
    }

    static async Task<int> TimedAsync(string what, Func<Task> f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await f().ConfigureAwait(false); return 0; } finally { Trace?.Invoke($"{what} took {sw.ElapsedMilliseconds} ms"); }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, PrinterDevice? Found)> ProbeCache = new();

    /// <summary>
    /// A Windows queue whose port is an IP address belongs to a network printer, but Bonjour does not always find that printer (with dozens of identical printers it
    /// often loses some). Such a queue would have no ink levels, firmware, serial number or web page, so the address is asked directly. Answers are remembered
    /// (a printer that did not answer for two minutes, one that did for half an hour); the search never waits more than a few seconds for them.
    /// </summary>
    internal static async Task EnrichByAddressAsync(List<PrinterDevice> devices, CancellationToken ct, Func<string, CancellationToken, Task<PrinterDevice?>>? probe = null)
    {
        probe ??= (ip, c) => ProbeAddressAsync(ip, c);
        var todo = devices.Where(d => d.Address is { Length: > 0 } && d.IppUri is null && d.EsclUri is null && d.SpoolerName is not null).ToList();
        if (todo.Count == 0) return;
        static void Apply(PrinterDevice d, PrinterDevice p) { d.IppUri ??= p.IppUri; d.EsclUri ??= p.EsclUri; d.WebUri ??= p.WebUri; }
        var gate = new SemaphoreSlim(8);   // not disposed: stragglers still use it after the search has moved on
        var all = Task.WhenAll(todo.Select(async d =>
        {
            var ip = d.Address!;
            if (ProbeCache.TryGetValue(ip, out var c) && DateTime.UtcNow - c.At < (c.Found is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(30)))
            {
                if (c.Found is not null) Apply(d, c.Found);
                return;
            }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var found = await probe(ip, ct).ConfigureAwait(false);
                ProbeCache[ip] = (DateTime.UtcNow, found);
                if (found is not null) Apply(d, found);
            }
            catch (OperationCanceledException) { throw; }
            catch { ProbeCache[ip] = (DateTime.UtcNow, null); }
            finally { gate.Release(); }
        }));
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(6), ct)).ConfigureAwait(false);   // the stragglers finish in the background and are remembered for the next search
    }

    public static async Task<List<PrinterDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var mdnsTask = Task.Run(async () => { var sw = System.Diagnostics.Stopwatch.StartNew(); try { return await MdnsClient.BrowseAsync(ct: ct); } finally { Trace?.Invoke($"Bonjour search took {sw.ElapsedMilliseconds} ms"); } }, ct);
        var local = await Task.Run(() => (
            Spooler: Timed("Windows printer list", () => SafeList(() => Retry(SpoolerPrinters.List))),
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
        MergeQueues(devices, local.Spooler, PrinterPicker.PresentContainers(), PrinterPicker.LiveContainer);

        // 3. USB HTTP interfaces (embedded web server over USB)
        MergeUsb(devices, local.Usb);

        // 4. WIA scanners
        foreach (var w in local.Wia)
        {
            var existing = devices.FirstOrDefault(d => PrinterDevice.Similar(d.Name, w.Name));
            if (existing is not null) existing.WiaDeviceId ??= w.Id;
            else devices.Add(new PrinterDevice { Name = w.Name, Model = w.Name, WiaDeviceId = w.Id });
        }

        await TimedAsync("Asking queues by address", () => EnrichByAddressAsync(devices, ct)).ConfigureAwait(false);

        foreach (var d in devices) if (d.Manufacturer == "") d.Manufacturer = d.Name.Split(' ')[0];
        Timed("USB presence", () => { PrinterPicker.MarkUsb(devices); return 0; });
        return devices.OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Folds the Windows print queues into <paramref name="devices"/>. Queues of one model normally are one printer (copies left over from earlier
    /// plugging), but two queues whose USB ports belong to two printers that are both plugged in right now are two printers (two L3250s): they stay
    /// two entries. Queues of printers that are not plugged in fold into the first similar entry, as before; the plugged-in ones go first so the
    /// entry carries a queue that works.
    /// </summary>
    internal static void MergeQueues(List<PrinterDevice> devices, IEnumerable<SpoolerPrinter> queues, IReadOnlyCollection<string> presentContainers,
        Func<string?, IReadOnlyCollection<string>, string?> liveContainer)
    {
        var live = new Dictionary<PrinterDevice, string>(ReferenceEqualityComparer.Instance);
        var ordered = queues.Select(q => (Queue: q, Live: liveContainer(q.Port, presentContainers))).OrderByDescending(x => x.Live is not null).ToList();
        foreach (var (q, liveC) in ordered)
        {
            if (q.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && q.Port.StartsWith("PORTPROMPT")) continue; // Print to PDF / XPS
            if (IsFaxQueue(q.Name)) continue;   // an MFP's fax function has its own Windows queue ("Fax - HP ...") that must not stand in for its print queue
            if (q.Name.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase) || q.Name.Contains("Fax", StringComparison.OrdinalIgnoreCase) && q.Port.StartsWith("SHRFAX")) continue;
            var ip = SpoolerPrinters.AddressFromPort(q.Port);
            var existing = devices.FirstOrDefault(d => ((ip is not null && d.Address == ip) || PrinterDevice.Similar(d.Name, q.Name) || PrinterDevice.Similar(d.Name, q.Driver))
                && !(liveC is not null && live.TryGetValue(d, out var other) && !string.Equals(other, liveC, StringComparison.OrdinalIgnoreCase))
                && !(ip is not null && d.Address is not null && d.Address != ip));   // a queue on another address is another printer, whatever it is called
            var rec = new PrinterDevice { Name = q.Name, Manufacturer = q.Driver.Split(' ')[0], Model = q.Name, Address = ip, SpoolerName = q.Name, SpoolerPort = q.Port, SpoolerDriver = q.Driver, SpoolerStatus = q.Status, SpoolerOffline = q.IsOffline };
            var target = existing ?? rec;
            if (existing is not null) existing.Merge(rec); else devices.Add(rec);
            if (liveC is not null) { live.TryAdd(target, liveC); target.UsbContainer ??= liveC; }
        }
    }

    /// <summary>
    /// Folds the USB web-services interfaces (what gives ink levels, firmware, serial number and the printer's web page over USB) into <paramref name="devices"/>.
    /// The interface of one physical printer goes to the entry of that printer: the one whose queue's USB port has the same container id. Matching by name
    /// alone put every interface on the first entry of the model, so with many printers of one model only one of them ever had supplies and a web page.
    /// </summary>
    internal static void MergeUsb(List<PrinterDevice> devices, IEnumerable<UsbInterfaceInfo> usb)
    {
        foreach (var grp in usb.GroupBy(u => (u.VendorId, u.ProductId, u.ContainerId)))
        {
            var first = grp.OrderByDescending(u => u.Openable).First();
            // an interface can carry a driver's label ("HP Smart Universal Printing(REST)") instead of the printer's model: prefer one that names the model
            var named = grp.OrderByDescending(u => u.Openable).FirstOrDefault(u => !IsGenericInterfaceName(CleanInterfaceName(u.Name))) ?? first;
            var modelName = CleanInterfaceName(named.Name);
            var rec = new PrinterDevice
            {
                Name = modelName, Manufacturer = first.VendorId == 0x03F0 ? "HP" : "", Model = modelName,
                Usb = grp.FirstOrDefault(u => u.Openable), UsbCandidates = grp.ToList(), UsbContainer = first.ContainerId,
            };
            string? container = first.ContainerId;
            // 1. the entry of this very printer; 2. an entry of the model that is not tied to another printer; otherwise a new entry (a printer without a queue)
            var existing = container is { Length: > 0 } ? devices.FirstOrDefault(d => string.Equals(d.UsbContainer, container, StringComparison.OrdinalIgnoreCase)) : null;
            existing ??= devices.FirstOrDefault(d => d.UsbContainer is null && (PrinterDevice.Similar(d.Name, modelName) || PrinterDevice.Similar(d.Model, modelName)));
            if (existing is not null) { existing.Merge(rec); existing.UsbContainer ??= container; }
            else devices.Add(rec);
        }
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
    /// <summary>A list that failed once (the spooler was busy adding a queue) is asked for again before the search settles for an empty one.</summary>
    static List<T> Retry<T>(Func<List<T>> f)
    {
        Exception? last = null;
        for (int i = 0; i < 3; i++)
        {
            try { return f(); } catch (Exception ex) { last = ex; Thread.Sleep(150); }
        }
        throw last!;
    }
}
