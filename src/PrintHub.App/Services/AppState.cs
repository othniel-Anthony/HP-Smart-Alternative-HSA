using Microsoft.UI.Dispatching;
using PrintHub.Core.Discovery;
using PrintHub.Core.Ipp;
using PrintHub.Core.Printing;
using PrintHub.Core.Settings;

namespace PrintHub.App.Services;

/// <summary>Shared state: discovered printers, the selected one, its live session and status.</summary>
public sealed class AppState
{
    DispatcherQueueTimer? _timer;
    int _statusGeneration;

    public AppSettings Settings { get; private set; } = new();
    public bool SettingsLoaded { get; private set; }
    public List<PrinterDevice> Devices { get; private set; } = new();
    public PrinterDevice? Current { get; private set; }
    public PrinterSession? Session { get; private set; }
    public PrinterStatus? Status { get; private set; }
    public string? StatusError { get; private set; }
    public bool Discovering { get; private set; }
    public bool Connecting { get; private set; }
    public SpoolerPrinter? Queue { get; private set; }

    public event Action? DevicesChanged;
    public event Action? CurrentChanged;
    public event Action? StatusChanged;

    public static string Version => typeof(AppState).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0";

    public async Task InitializeAsync()
    {
        Settings = await Task.Run(SettingsStore.Load);
        SettingsLoaded = true;
        StartupTrace.Mark("Settings loaded");
        // show remembered printers immediately, then discover
        Devices = Settings.Printers.Select(p => p.ToDevice()).ToList();
        DevicesChanged?.Invoke();
        App.Window.ApplyTheme(Settings.Theme);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += async (_, _) => { if (!PrinterActivity.IsBusy) await RefreshStatusAsync(); };
        _timer.Start();

        // A printer plugged in, switched on or added in Windows while HSA is open is noticed within a few seconds (see WatchAsync).
        _watch = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _watch.Interval = TimeSpan.FromSeconds(4);
        _watch.Tick += async (_, _) => await WatchAsync();
        _watch.Start();

        // The printer used last time is usually still where it was: connect to it right away instead of waiting for the
        // network search (about 3 s of Bonjour) to finish. The search below then completes the picture (USB, Windows queue, scanners).
        var remembered = Devices.FirstOrDefault(d => d.Id == Settings.SelectedPrinterId && d.HasNetwork);
        if (remembered is not null) _earlySelect = SelectAsync(remembered);

        await DiscoverAsync();
        StartupTrace.Mark("Start-up finished (printer chosen, status requested)");
    }

    Task? _earlySelect;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _watch;
    string? _fingerprint;
    DateTime _lastSearch = DateTime.UtcNow;

    /// <summary>How often a search runs even when nothing local changed (network printers appear without any sign on this computer).</summary>
    static readonly TimeSpan BackgroundSearchEvery = TimeSpan.FromMinutes(3);

    /// <summary>Called every few seconds: searches again when the USB / Windows printers changed, and now and then for network printers.</summary>
    async Task WatchAsync()
    {
        if (Discovering || !SettingsLoaded || _fingerprint is null || PrinterActivity.IsBusy) return;   // not while a maintenance run has the printer's USB port
        string now;
        try { now = await Task.Run(PrinterDiscovery.Fingerprint); } catch { return; }
        bool changed = now != _fingerprint;
        if (!changed && DateTime.UtcNow - _lastSearch < BackgroundSearchEvery) return;
        AppLog.Write(changed ? "Printers were added, removed or switched: searching again" : "Searching for network printers again (periodic)");
        await DiscoverAsync(background: true);
    }
    /// <summary>The user picked a printer by hand in this session: a search must not take the choice away.</summary>
    bool _userChose;
    /// <summary>Names of the printers that were plugged in by USB (and online) at the previous search.</summary>
    List<string> _usbNames = new();

    /// <summary>A printer chosen by the user (from the printer list or added by address).</summary>
    public async Task ChooseAsync(PrinterDevice? device) { _userChose = true; await SelectAsync(device); }

    /// <summary>True when a session opened for <paramref name="a"/> would be identical to one opened for <paramref name="b"/>.</summary>
    bool SameConnection(PrinterDevice a, PrinterDevice b)
    {
        bool UsbFirst(PrinterDevice d) => d.HasUsbHttp && (Settings.PreferUsb || !d.HasNetwork);
        return a.IppUri == b.IppUri && a.EsclUri == b.EsclUri && a.WebUri == b.WebUri && UsbFirst(a) == UsbFirst(b) && !UsbFirst(a);
    }

    static string DeviceSummary(IEnumerable<PrinterDevice> devices) =>
        string.Join("\n", devices.Select(d => $"{d.Id}|{d.Name}|{d.Connection}|{d.OnUsb}").OrderBy(x => x, StringComparer.Ordinal));

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); } catch (Exception ex) { AppLog.Write("Saving settings failed: " + ex.Message); }
    }

    /// <param name="background">A search HSA started by itself: the window shows nothing until the list really changed, and the printer in use stays connected.</param>
    public async Task DiscoverAsync(bool background = false)
    {
        if (Discovering) return;
        Discovering = true; if (!background) DevicesChanged?.Invoke();
        string listBefore = DeviceSummary(Devices);
        string fingerprint = "";
        try { fingerprint = await Task.Run(PrinterDiscovery.Fingerprint); } catch { }
        try
        {
            StartupTrace.Mark("Printer search started");
            var found = await PrinterDiscovery.DiscoverAsync();
            StartupTrace.Mark("Printer search finished");
            // keep remembered printers that discovery didn't see (e.g. powered off, other subnet)
            foreach (var saved in Settings.Printers)
                if (!found.Any(f => f.Id == saved.Id || (f.Address != null && f.Address == saved.Address) || PrinterDevice.Similar(f.Name, saved.Name)))
                    found.Add(saved.ToDevice());

            // a search HSA started itself must not disturb the printer in use: when nothing about it changed, keep the very same object (and its connection)
            if (background && Current is not null && PrinterDevice.FindSame(found, Current) is { } same
                && same.Connection == Current.Connection && same.CanScan == Current.CanScan && same.CanPrint == Current.CanPrint && same.OnUsb == Current.OnUsb)
                found[found.IndexOf(same)] = Current;

            Devices = found;
            AppLog.Write($"Discovery{(background ? " (automatic)" : "")}: {found.Count} printer(s): {string.Join(", ", found.Select(d => $"{d.Name} [{d.Connection}]"))}");
        }
        catch (Exception ex) { if (!background) { AppLog.Write("Discovery failed: " + ex); App.Window.Toast("Printer search failed: " + ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error); } else AppLog.Write("Automatic discovery failed: " + ex.Message); }
        finally { Discovering = false; _lastSearch = DateTime.UtcNow; _fingerprint = fingerprint.Length > 0 ? fingerprint : _fingerprint; }
        if (!background || DeviceSummary(Devices) != listBefore) DevicesChanged?.Invoke();

        if (_earlySelect is { } early) { _earlySelect = null; try { await early; } catch { } }

        // re-select the previously used printer (or the only one)
        var keep = Current is null ? null : PrinterDevice.FindSame(Devices, Current);

        // A printer that is plugged in by USB and online is the one the user wants to use: pick it without asking. (Until the user picks one by
        // hand, it wins over the remembered printer; afterwards only a printer plugged in since the last search is switched to.)
        var usbPick = Environment.GetEnvironmentVariable("HSA_NO_USB_AUTOSELECT") == "1" ? null : PrinterPicker.PickUsb(Devices, Current, Settings.SelectedPrinterId, SpoolerPrinters.GetDefault(), _userChose, _usbNames);
        _usbNames = Devices.Where(PrinterPicker.IsOnlineOnUsb).Select(d => d.Name).ToList();
        bool switchedAway = false;
        if (usbPick is not null)
        {
            if (keep is not null && (usbPick.Id == keep.Id || usbPick.Name == keep.Name)) usbPick = keep;   // already on it
            else if (Current is not null && !Connecting) switchedAway = true;
            AppLog.Write($"USB printer chosen automatically: {usbPick.Name}");
        }

        var wanted = usbPick
            ?? keep
            ?? Devices.FirstOrDefault(d => d.Id == Settings.SelectedPrinterId)
            ?? Devices.FirstOrDefault(d => d.SpoolerName != null && SpoolerPrinters.GetDefault() == d.SpoolerName)
            ?? (Devices.Count == 1 ? Devices[0] : null);
        if (wanted is null || (Current is not null && ReferenceEquals(Current, wanted))) return;

        if (Current is not null && Session is not null && !Connecting && keep is not null && ReferenceEquals(keep, wanted) && SameConnection(Current, wanted))
        {
            // already connected to this printer the same way (the early connection): just pick up what the search added
            // Stay quiet unless something visible changed: pages rebuild on CurrentChanged, which would throw away
            // options the user has already picked while the search was running.
            var before = Current;
            Current = wanted;
            if (Settings.SelectedPrinterId != wanted.Id) { Settings.SelectedPrinterId = wanted.Id; SaveSettings(); }
            if (before.Name != wanted.Name || before.Connection != wanted.Connection || before.CanScan != wanted.CanScan || before.CanPrint != wanted.CanPrint)
                CurrentChanged?.Invoke();
            if (wanted.SpoolerName is not null) await RefreshStatusAsync(); // pick up the Windows queue state
        }
        else
        {
            await SelectAsync(wanted);
            if (switchedAway) App.Window.Toast($"Switched to {wanted.Name}: it is connected by USB and online.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
        }
    }

    public async Task SelectAsync(PrinterDevice? device)
    {
        if (device is null) return;
        Connecting = true; Current = device; Status = null; StatusError = null; Queue = null;
        Settings.SelectedPrinterId = device.Id; SaveSettings();
        CurrentChanged?.Invoke();

        var old = Session; Session = null;
        if (old is not null) await old.DisposeAsync();

        try
        {
            Session = await PrinterSession.OpenAsync(device, Settings.PreferUsb);
            if (Session.Error is not null) AppLog.Write($"Session for {device.Name}: {Session.Error}");
        }
        catch (Exception ex) { AppLog.Write("Opening session failed: " + ex); StatusError = ex.Message; }
        Connecting = false;
        CurrentChanged?.Invoke();
        await RefreshStatusAsync();
    }

    public async Task RefreshStatusAsync()
    {
        var dev = Current; var session = Session;
        if (dev is null) return;
        int gen = ++_statusGeneration;

        PrinterStatus? status = null; string? error = null; SpoolerPrinter? queue = null;
        if (session?.Ipp is { } ipp)
        {
            try { status = await ipp.GetStatusAsync(); }
            catch (Exception ex) { error = ex is HttpRequestException or TaskCanceledException ? "The printer did not respond. Is it switched on and connected?" : ex.Message; }
        }
        else if (session is { Error: not null }) error = session.Error;

        // IPP gave no ink levels (or there is no IPP at all, as on many USB-only HP inkjets): try HP's web services.
        if (session is not null && (status is null || status.Supplies.Count == 0) && await session.GetLedmSuppliesAsync() is { } ledm)
        {
            status ??= new PrinterStatus { MakeAndModel = dev.Name, State = PrinterState.Idle };
            status.Supplies.AddRange(ledm);
            error = null;
        }

        if (dev.SpoolerName is not null)
            queue = await Task.Run(() => SpoolerPrinters.List().FirstOrDefault(q => q.Name == dev.SpoolerName));

        if (gen != _statusGeneration || !ReferenceEquals(dev, Current)) return; // a newer refresh/selection won
        Status = status; StatusError = error; Queue = queue;
        StatusChanged?.Invoke();
    }

    public async Task<PrinterDevice?> AddByAddressAsync(string host)
    {
        var dev = await PrinterDiscovery.ProbeAddressAsync(host);
        if (dev is null) return null;
        Settings.Printers.RemoveAll(p => p.Id == dev.Id || p.Address == dev.Address);
        Settings.Printers.Add(SavedPrinter.From(dev));
        SaveSettings();
        Devices.RemoveAll(d => d.Id == dev.Id || (d.Address != null && d.Address == dev.Address));
        Devices.Add(dev);
        DevicesChanged?.Invoke();
        await ChooseAsync(dev);
        return dev;
    }

    public void ForgetPrinter(PrinterDevice dev)
    {
        Settings.Printers.RemoveAll(p => p.Id == dev.Id);
        SaveSettings();
    }

    /// <summary>Plain-language state for the header and Home page.</summary>
    public string StateText
    {
        get
        {
            if (Current is null) return "No printer selected";
            if (Connecting) return "Connecting…";
            if (StatusError is not null && Status is null && Queue is null) return "Not reachable";
            if (Status is not null)
            {
                var alerts = Status.Alerts.ToList();
                if (Status.State == PrinterState.Stopped) return alerts.FirstOrDefault() ?? "Stopped";
                if (alerts.Count > 0 && Status.State != PrinterState.Processing) return alerts[0];
                return Status.State switch { PrinterState.Idle => "Ready", PrinterState.Processing => "Printing…", _ => "Connected" };
            }
            if (Queue is not null) return Queue.StatusText;
            return Current.CanScan || Current.CanPrint ? "Connected" : "Unknown";
        }
    }

    public bool IsReady => Current is not null && !Connecting && (Status is not null ? Status.State != PrinterState.Stopped : Queue is not null ? !Queue.IsOffline : Current.CanPrint || Current.CanScan);
}
