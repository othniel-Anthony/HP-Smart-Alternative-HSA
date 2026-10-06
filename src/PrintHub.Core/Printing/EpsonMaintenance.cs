using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PrintHub.Core.Discovery;
using PrintHub.Core.Usb;
using static PrintHub.Core.Usb.NativeMethods;

namespace PrintHub.Core.Printing;

public enum EpsonCleaning { All, Black, Colour }

/// <summary>
/// Maintenance for Epson inkjets: print a nozzle check, clean the print head, and reset the printer's job state.
/// The commands are Epson's "remote mode" commands, sent straight to the printer's USB print interface (the same way the open-source
/// escputil tool does), so they work with or without Epson's own utilities. Nothing here changes any counter or setting stored in the printer.
/// </summary>
public static class EpsonMaintenance
{
    const int EpsonVendor = 0x04B8;
    static readonly Guid UsbPrintInterface = new("28D78FAD-5A12-11D1-AE5B-0000F803A8C2");

    // ---- the bytes (kept separate and public so they can be checked) ----

    /// <summary>Leave any packet mode and reset the printer's input: NUL padding, the EJL "1284.4" header, then ESC @.</summary>
    public static byte[] Init() => Latin1("\0\0\0\x1B\x01@EJL 1284.4\n@EJL     \n\x1B@");

    /// <summary>ESC @ then ESC ( R "REMOTE1": the printer now accepts two-letter maintenance commands.</summary>
    public static byte[] EnterRemote() => Latin1("\x1B@\x1B(R\x08\0\0REMOTE1");

    public static byte[] ExitRemote() => new byte[] { 0x1B, 0, 0, 0, 0x1B, 0x40 };

    /// <summary>Leave remote mode without the ESC @ reset that <see cref="ExitRemote"/> adds. A reset after a printed page can leave the sheet in the printer.</summary>
    public static byte[] ExitRemoteNoReset() => new byte[] { 0x1B, 0, 0, 0 };

    /// <summary>Carriage return and form feed: the end-of-page signal that makes the printer push the sheet out.</summary>
    public static byte[] EndOfPage() => new byte[] { 0x0D, 0x0C };

    /// <summary>Two ASCII letters, a 16-bit parameter count, then the parameters.</summary>
    public static byte[] Command(string name, params byte[] args)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes(name)) { (byte)args.Length, (byte)(args.Length >> 8) };
        b.AddRange(args);
        return b.ToArray();
    }

    // These end inside remote mode. The exit (ExitRemote) is sent afterwards as its own write: on a real printer an exit that arrives
    // together with a query makes it drop the reply.
    /// <summary>
    /// "CH" with the nozzle group (0 all, 1 black, 2 colours). Setting bit 0x10 on that byte makes it Epson's power cleaning, which pushes far more
    /// ink through the print head (and into the waste pads) than a normal cleaning.
    /// </summary>
    public static byte[] BuildClean(EpsonCleaning which, bool power = false) =>
        Concat(Init(), EnterRemote(), Command("CH", 0, (byte)((which switch { EpsonCleaning.Black => 1, EpsonCleaning.Colour => 2, _ => 0 }) | (power ? 0x10 : 0))));

    /// <summary>A single "NC" (print nozzle check) command, as the maintained epson_print_conf tool sends it. An older sequence with two extra commands left an L3250 stuck in an error state with the page half out.</summary>
    public static byte[] BuildNozzleCheck() =>
        Concat(Init(), EnterRemote(), Command("NC", 0, 0));

    public static byte[] BuildStatusQuery() => Concat(Init(), EnterRemote(), Command("ST", 0, 1));

    internal const string NoActivity = "no-activity";

    internal static void ThrowIfError(string? finalStatus, string what)
    {
        if (finalStatus == NoActivity)
            throw new InvalidOperationException($"The printer did not react to the command for {what}: it stayed idle. Some models only accept this from Epson's own maintenance utility. Use “Open the Epson driver settings” below and look for its Maintenance tab, or print a nozzle check to see whether anything changed.");
        if (finalStatus == "00")
            throw new InvalidOperationException($"The printer reported an error while doing {what} (status 00). Check that paper is loaded and nothing is jammed, look at the printer's lights, then try again. If a sheet is stuck halfway, switch the printer off and on and pull it out gently.");
    }

    static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ---- actions ----

    /// <summary>The printer's own status code ("ST:xx;"), or null when it did not answer. Used to confirm the USB link works before sending anything.</summary>
    public static async Task<string?> QueryStatusAsync(PrinterDevice dev, CancellationToken ct = default)
    {
        using var port = OpenPort(dev);
        var reply = await port.ExchangeAsync(BuildStatusQuery(), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var text = Encoding.Latin1.GetString(reply);
        int i = text.IndexOf("ST:", StringComparison.Ordinal);
        if (i < 0) return null;
        int end = text.IndexOf(';', i);
        return end > i ? text[(i + 3)..end] : null;
    }

    public static async Task NozzleCheckAsync(PrinterDevice dev, CancellationToken ct = default)
    {
        using var port = OpenPort(dev);
        Diag.Log($"Epson: nozzle check on '{dev.Name}' via {port.Path}");
        ThrowIfError(await port.RunAsync(BuildNozzleCheck(), ct, TimeSpan.FromMinutes(3), endPage: true).ConfigureAwait(false), "the nozzle check");
    }

    /// <summary>
    /// Clean the print head. <paramref name="cycles"/> is the cleaning level: 1 runs one full cleaning, 2 runs two in a row, 3 runs three
    /// (each cycle waits for the printer to finish and rest briefly before the next). Optionally prints a nozzle check at the end.
    /// </summary>
    public static async Task CleanHeadAsync(PrinterDevice dev, EpsonCleaning which, int cycles = 1, bool nozzleCheckAfter = false,
        IProgress<string>? progress = null, CancellationToken ct = default, bool power = false)
    {
        if (power) cycles = 1; // a power flush is already the strongest single step; it is never repeated automatically
        Diag.Log($"Epson: head cleaning ({which}{(power ? ", POWER" : "")}, {cycles} cycle(s)) on '{dev.Name}'");
        await CleanCyclesAsync(() => OpenPort(dev), which, Math.Clamp(cycles, 1, 3), TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(power ? 10 : 5), progress, ct, power).ConfigureAwait(false);
        if (nozzleCheckAfter)
        {
            progress?.Report("Printing the nozzle check…");
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await NozzleCheckAsync(dev, ct).ConfigureAwait(false);
        }
    }

    internal static async Task CleanCyclesAsync(Func<Port> open, EpsonCleaning which, int cycles, TimeSpan rest, TimeSpan maxPerCycle,
        IProgress<string>? progress, CancellationToken ct, bool power = false)
    {
        for (int i = 1; i <= cycles; i++)
        {
            progress?.Report(power ? "Power flushing…" : cycles == 1 ? "Cleaning…" : $"Cleaning {i} of {cycles}…");
            using (var port = open()) ThrowIfError(await port.RunAsync(BuildClean(which, power), ct, maxPerCycle, expectActivity: true).ConfigureAwait(false), power ? "the power flush" : "the cleaning");
            if (i < cycles)
            {
                progress?.Report($"Cleaning {i} of {cycles} finished. Resting a moment…");
                await Task.Delay(rest, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Clears the jobs waiting on the printer's Windows queue (when HSA is allowed to) and sends the printer a standard reset so it drops
    /// anything half-received. Returns a plain-language summary of what was done.
    /// </summary>
    public static async Task<string> ResetAsync(PrinterDevice dev, CancellationToken ct = default)
    {
        var done = new List<string>();
        if (dev.SpoolerName is not null)
        {
            if (SpoolerPrinters.Purge(dev.SpoolerName, out var why)) done.Add("cleared the Windows print queue");
            else { Diag.Log("Epson reset: could not clear the queue: " + why); done.Add($"could not clear the Windows print queue ({why})"); }
        }
        using (var port = OpenPort(dev))
        {
            Diag.Log($"Epson: reset on '{dev.Name}' via {port.Path}");
            await port.SendAsync(Init(), ct).ConfigureAwait(false);
            done.Add("sent the printer a reset");
        }
        return string.Join("; ", done);
    }

    // ---- finding and opening the printer's USB print interface ----

    static readonly List<Port> OpenPorts = new();

    /// <summary>
    /// Takes every open printer connection out of remote-command mode. Called when the window closes: a clean, a nozzle check or a power flush that is
    /// still running when HSA is closed must not leave the printer waiting for more remote commands (it then stays busy long after the job is done).
    /// Leaving remote mode does not stop a cleaning the printer has already started.
    /// </summary>
    public static void ReleaseAll()
    {
        Port[] ports; lock (OpenPorts) ports = OpenPorts.ToArray();
        foreach (var p in ports) p.LeaveRemoteModeNow();
    }

    internal sealed class Port : IDisposable
    {
        readonly Stream _fs;
        public string Path { get; }
        /// <summary>Remote commands have been sent on this connection and the command to leave remote mode has not.</summary>
        internal volatile bool InRemoteMode;
        public Port(string path, Stream fs) { Path = path; _fs = fs; lock (OpenPorts) OpenPorts.Add(this); }

        /// <summary>Best effort and quick: used when the program is closing or a run failed half way. Never throws.</summary>
        internal void LeaveRemoteModeNow()
        {
            if (!InRemoteMode) return;
            try
            {
                var exit = ExitRemoteNoReset();   // no reset: a reset would cut a page or a cleaning short
                // a thread of its own, not the shared pool: this must work while the program is closing or the pool is busy
                var write = new Thread(() => { try { _fs.Write(exit, 0, exit.Length); _fs.Flush(); } catch { } }) { IsBackground = true };
                write.Start();
                bool done = write.Join(TimeSpan.FromSeconds(2));
                Diag.Log($"Epson: told the printer to leave remote mode (the run ended early or HSA is closing): {(done ? "sent" : "no answer in 2 s")}");
            }
            catch { /* the port may already be gone */ }
            InRemoteMode = false;
        }

        // pacing; tests shorten these
        public TimeSpan StartDelay { get; init; } = TimeSpan.FromMilliseconds(1500);
        public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan NoBusyGrace { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan ErrorGrace { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan ActivityWindow { get; init; } = TimeSpan.FromSeconds(9);

        public async Task SendAsync(byte[] data, CancellationToken ct)
        {
            await _fs.WriteAsync(data, ct).ConfigureAwait(false);
            await _fs.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Send commands, then keep asking the printer for its status until it is idle again, and only then leave remote mode.
        /// Leaving earlier (the exit ends with ESC @, a reset) cuts a nozzle-check page off half way.
        /// </summary>
        public async Task<string?> RunAsync(byte[] commands, CancellationToken ct, TimeSpan maxWait, bool endPage = false, bool expectActivity = false)
        {
            InRemoteMode = true;
            try { return await RunCoreAsync(commands, ct, maxWait, endPage, expectActivity).ConfigureAwait(false); }
            finally { LeaveRemoteModeNow(); }   // a cancelled or failed run must still let the printer leave remote mode
        }

        async Task<string?> RunCoreAsync(byte[] commands, CancellationToken ct, TimeSpan maxWait, bool endPage, bool expectActivity)
        {
            await SendAsync(commands, ct).ConfigureAwait(false);
            await Task.Delay(StartDelay, ct).ConfigureAwait(false);

            var started = DateTime.UtcNow;
            DateTime? errorSince = null;
            bool sawBusy = false; int idleInARow = 0;
            string? last = null;
            while (DateTime.UtcNow - started < maxWait)
            {
                var code = await PollStatusAsync(ct).ConfigureAwait(false);
                last = code;
                Diag.Log($"Epson: status {code ?? "(no answer)"} after {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
                bool idle = code == "04";
                if (!idle) sawBusy = true; // no answer at all usually means it is too busy printing to talk

                // A command the printer does not understand leaves it plainly idle (04) the whole time. Cleaning always takes tens of seconds,
                // so a printer that is still idle after the activity window ignored the command: say so instead of claiming it worked.
                if (expectActivity && !sawBusy && DateTime.UtcNow - started > ActivityWindow) { last = NoActivity; break; }
                idleInARow = idle ? idleInARow + 1 : 0;
                // idle counts only once the printer has been seen working; a printer that never shows as busy is given a grace period, then released
                if (idleInARow >= 2 && (sawBusy || DateTime.UtcNow - started > NoBusyGrace)) break;

                // "00" is the printer's error state (no paper, paper jam, ink problem...). Waiting longer will not fix it.
                if (code == "00") { errorSince ??= DateTime.UtcNow; if (DateTime.UtcNow - errorSince > ErrorGrace) break; }
                else errorSince = null;
                await Task.Delay(PollEvery, ct).ConfigureAwait(false);
            }
            if (endPage && last != "00")
            {
                // The page is printed but still in the printer. Leave remote mode WITHOUT a reset, then send the end-of-page: the sheet comes out.
                // (Checked on real printers: a "job end" command here makes the printer print the pattern a second time.)
                await SendAsync(ExitRemoteNoReset(), ct).ConfigureAwait(false);
                InRemoteMode = false;
                await SendAsync(EndOfPage(), ct).ConfigureAwait(false);
            }
            else { await SendAsync(ExitRemote(), ct).ConfigureAwait(false); InRemoteMode = false; }
            return last;
        }

        /// <summary>Ask for the status while already in remote mode and return the "ST:xx" code, or null when there was no answer.</summary>
        async Task<string?> PollStatusAsync(CancellationToken ct)
        {
            await SendAsync(Command("ST", 0, 1), ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var rx = new byte[1024]; int total = 0;
            try
            {
                while (total < rx.Length)
                {
                    int n = await _fs.ReadAsync(rx.AsMemory(total), cts.Token).ConfigureAwait(false);
                    if (n <= 0) { await Task.Delay(100, cts.Token).ConfigureAwait(false); continue; }
                    total += n;
                    var text = Encoding.Latin1.GetString(rx, 0, total);
                    int i = text.LastIndexOf("ST:", StringComparison.Ordinal);
                    if (i >= 0) { int end = text.IndexOf(';', i); if (end > i) return text[(i + 3)..end]; }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            return null;
        }

        /// <summary>Send a query and collect the reply until it contains "ST:xx;" (or the printer goes quiet), then leave remote mode.</summary>
        public async Task<byte[]> ExchangeAsync(byte[] query, TimeSpan wait, CancellationToken ct)
        {
            InRemoteMode = true;
            try { return await ExchangeCoreAsync(query, wait, ct).ConfigureAwait(false); }
            finally { LeaveRemoteModeNow(); }
        }

        async Task<byte[]> ExchangeCoreAsync(byte[] query, TimeSpan wait, CancellationToken ct)
        {
            await SendAsync(query, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            var rx = new byte[4096]; int total = 0;
            try
            {
                while (total < rx.Length)
                {
                    int n = await _fs.ReadAsync(rx.AsMemory(total), cts.Token).ConfigureAwait(false);
                    if (n <= 0) { await Task.Delay(100, cts.Token).ConfigureAwait(false); continue; } // the USB port returns empty reads while it has nothing to say
                    total += n;
                    var text = Encoding.Latin1.GetString(rx, 0, total);
                    int st = text.IndexOf("ST:", StringComparison.Ordinal);
                    if (st >= 0 && text.IndexOf(';', st) > st) break; // the answer to our question; earlier "PS" lines are the printer's own chatter
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* nothing more is coming */ }
            try { await SendAsync(ExitRemote(), ct).ConfigureAwait(false); InRemoteMode = false; } catch (IOException) { }
            return rx.AsSpan(0, total).ToArray();
        }

        public void Dispose()
        {
            LeaveRemoteModeNow();
            lock (OpenPorts) OpenPorts.Remove(this);
            _fs.Dispose();
        }
    }

    static Port OpenPort(PrinterDevice dev)
    {
        var stream = OpenUsbStream(dev, out var path);
        return new Port(path, stream);
    }

    /// <summary>Open the printer's USB print interface for reading and writing (asynchronous), or explain why that is not possible.</summary>
    internal static FileStream OpenUsbStream(PrinterDevice dev, out string path, int vendor = EpsonVendor)
    {
        path = FindPrintInterface(dev, vendor)
            ?? throw new InvalidOperationException(dev.HasNetwork && !dev.UsbCandidates.Any() && dev.SpoolerPort?.StartsWith("USB", StringComparison.OrdinalIgnoreCase) != true
                ? "These tools talk to the printer over its USB cable. Connect it with USB (it can stay on Wi-Fi too), or use the Maintenance tab of the Epson driver."
                : $"HSA could not find {dev.Name} on a USB port. Check that it is switched on and plugged in.");

        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Windows would not let HSA open the printer's USB port ({new System.ComponentModel.Win32Exception(err).Message}). Close any other program that is using the printer and try again.");
        }
        return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
    }

    /// <summary>The USB print interfaces that are plugged in right now, with the name Windows gives each.</summary>
    public static List<(string Path, string Name, int Vendor, int Product, string Container)> PresentPrintInterfaces()
    {
        var result = new List<(string, string, int, int, string)>();
        var guid = UsbPrintInterface;
        if (CM_Get_Device_Interface_List_SizeW(out var len, ref guid, null, 0) != 0 || len <= 1) return result;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0) != 0) return result;

        foreach (var path in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // \\?\USB#VID_04B8&PID_118A&MI_01#6&14c9723f&0&0001#{guid}  ->  USB\VID_04B8&PID_118A&MI_01\6&14c9723f&0&0001
            var parts = path.TrimStart('\\', '?').Split('#');
            if (parts.Length < 3 || !parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(parts[1], @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string name = "", container = "";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{parts[1]}\{parts[2]}");
                var raw = key?.GetValue("FriendlyName") as string ?? key?.GetValue("DeviceDesc") as string ?? "";
                name = raw[(raw.LastIndexOf(';') + 1)..];
                container = key?.GetValue("ContainerID") as string ?? "";
            }
            catch { }
            result.Add((path, name, Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16), container));
        }
        return result;
    }

    /// <summary>
    /// Which plugged-in USB print interface belongs to this printer (never its FAX function), or null when that cannot be established.
    /// Matching is strict on purpose: with two Epsons around, a wrong guess would send the command to the wrong printer.
    /// </summary>
    public static string? FindPrintInterface(PrinterDevice dev, int vendor = EpsonVendor)
    {
        var epson = PresentPrintInterfaces().Where(p => p.Vendor == vendor && !p.Name.Contains("fax", StringComparison.OrdinalIgnoreCase)).ToList();
        if (epson.Count == 0) return null;

        var byName = epson.Where(p => PrinterDevice.Similar(p.Name, dev.Name)).ToList();
        if (byName.Count > 0) return byName[0].Path; // the same model plugged in twice: either is as good as the other

        // otherwise follow the Windows queue's USB port to the physical device it was installed for
        if (dev.SpoolerPort is { Length: > 0 } port && ContainerOfPort(port) is { Length: > 0 } container)
            return epson.FirstOrDefault(p => string.Equals(p.Container, container, StringComparison.OrdinalIgnoreCase)).Path;
        return null;
    }

    internal static string? ContainerOfPort(string port)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBPRINT");
            if (root is null) return null;
            foreach (var model in root.GetSubKeyNames())
            {
                using var mk = root.OpenSubKey(model);
                if (mk is null) continue;
                foreach (var inst in mk.GetSubKeyNames())
                {
                    using var ik = mk.OpenSubKey(inst);
                    using var dp = ik?.OpenSubKey("Device Parameters");
                    if (string.Equals(dp?.GetValue("PortName") as string, port, StringComparison.OrdinalIgnoreCase)) return ik?.GetValue("ContainerID") as string;
                }
            }
        }
        catch { }
        return null;
    }
}
