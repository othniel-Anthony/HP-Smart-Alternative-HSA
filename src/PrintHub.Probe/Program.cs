using PrintHub.Core.Usb;

// usage:
//   probe list                    list USB interfaces that carry HTTP (IPP-USB / HP web services)
//   probe list-all                include remembered (unplugged) devices
//   probe get [path] [index]      open the interface, GET a path (default /), print status + headers + start of body
//   probe proxy [index]           run the loopback proxy until Enter; browse to the printed URL for the EWS

var cmd = args.Length > 0 ? args[0] : "list";

if (cmd is "list" or "list-all")
{
    var items = UsbDeviceScanner.Scan(presentOnly: cmd == "list").Where(i => i.HttpKind != UsbHttpKind.None).ToList();
    if (items.Count == 0) Console.WriteLine("No HTTP-capable USB printer interfaces found. Is the printer plugged in and powered on?");
    for (int i = 0; i < items.Count; i++) Console.WriteLine($"[{i}] {items[i].Describe()}");
    return;
}

if (cmd == "discover")
{
    var devs = await PrintHub.Core.Discovery.PrinterDiscovery.DiscoverAsync();
    foreach (var d in devs)
    {
        Console.WriteLine($"* {d.Name}  [{d.Connection}]  hp={d.IsHp}  addr={d.Address}");
        Console.WriteLine($"    ipp={d.IppUri}  escl={d.EsclUri}  web={d.WebUri}  spooler={d.SpoolerName}  usb={d.Usb?.InstanceId}");
        if (d.IppUri is not null)
        {
            try
            {
                var st = await new PrintHub.Core.Ipp.IppClient(d.IppUri).GetStatusAsync();
                Console.WriteLine($"    state={st.State} model={st.MakeAndModel} fw={st.Firmware} sn={st.SerialNumber} alerts=[{string.Join("; ", st.Alerts)}]");
                foreach (var s in st.Supplies) Console.WriteLine($"      {s.Name} ({s.Type}) {s.Percent}%");
            }
            catch (Exception ex) { Console.WriteLine($"    IPP error: {ex.Message}"); }
        }
        if (d.EsclUri is not null)
        {
            try
            {
                var c = await new PrintHub.Core.Escl.EsclClient(d.EsclUri).GetCapabilitiesAsync();
                Console.WriteLine($"    eSCL {c.Version} flatbed={(c.Flatbed is null ? "no" : $"{c.Flatbed.MaxWidth}x{c.Flatbed.MaxHeight} dpi=[{string.Join(",", c.Flatbed.Resolutions)}]")} feeder={(c.Feeder is null ? "no" : "yes")} duplex={c.FeederDuplex}");
            }
            catch (Exception ex) { Console.WriteLine($"    eSCL error: {ex.Message}"); }
        }
    }
    if (devs.Count == 0) Console.WriteLine("Nothing found.");
    return;
}

var candidates = UsbDeviceScanner.FindHttpInterfaces().Where(i => i.Openable).ToList();
if (candidates.Count == 0)
{
    Console.WriteLine("No openable (WinUSB-bound, present) HTTP interface. Run 'probe list' to see what Windows reports.");
    return;
}

int index = int.TryParse(args.LastOrDefault(), out var ix) ? ix : 0;
var iface = candidates[Math.Clamp(index, 0, candidates.Count - 1)];
Console.WriteLine($"Using {iface.Describe()}");

await using var proxy = new UsbHttpProxy(iface);
proxy.Log += s => Console.WriteLine("  " + s);
proxy.Start();

if (cmd == "get")
{
    var path = args.Length > 1 && args[1].StartsWith('/') ? args[1] : "/";
    using var http = new HttpClient { BaseAddress = proxy.BaseUri, Timeout = TimeSpan.FromSeconds(30) };
    using var resp = await http.GetAsync(path);
    Console.WriteLine($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
    foreach (var h in resp.Headers.Concat(resp.Content.Headers)) Console.WriteLine($"{h.Key}: {string.Join(", ", h.Value)}");
    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine(body[..Math.Min(body.Length, 1500)]);
}
else if (cmd == "proxy")
{
    Console.WriteLine($"Embedded web server available at {proxy.BaseUri}  (press Enter to stop)");
    Console.ReadLine();
}
