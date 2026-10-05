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
        StartupTrace.Mark("Settings loaded");
        // show remembered printers immediately, then discover
        Devices = Settings.Printers.Select(p => p.ToDevice()).ToList();
        DevicesChanged?.Invoke();
        App.Window.ApplyTheme(Settings.Theme);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();

        // The printer used last time is usually still where it was: connect to it right away instead of waiting for the
        // network search (about 3 s of Bonjour) to finish. The search below then completes the picture (USB, Windows queue, scanners).
        var remembered = Devices.FirstOrDefault(d => d.Id == Settings.SelectedPrinterId && d.HasNetwork);
        if (remembered is not null) _earlySelect = SelectAsync(remembered);

        await DiscoverAsync();
        StartupTrace.Mark("Start-up finished (printer chosen, status requested)");
    }

    Task? _earlySelect;

    /// <summary>True when a session opened for <paramref name="a"/> would be identical to one opened for <paramref name="b"/>.</summary>
    bool SameConnection(PrinterDevice a, PrinterDevice b)
    {
        bool UsbFirst(PrinterDevice d) => d.HasUsbHttp && (Settings.PreferUsb || !d.HasNetwork);
        return a.IppUri == b.IppUri && a.EsclUri == b.EsclUri && a.WebUri == b.WebUri && UsbFirst(a) == UsbFirst(b) && !UsbFirst(a);
    }

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); } catch (Exception ex) { AppLog.Write("Saving settings failed: " + ex.Message); }
    }

    public async Task DiscoverAsync()
    {
        if (Discovering) return;
        Discovering = true; DevicesChanged?.Invoke();
        try
        {
            StartupTrace.Mark("Printer search started");
            var found = await PrinterDiscovery.DiscoverAsync();
            StartupTrace.Mark("Printer search finished");
            // keep remembered printers that discovery didn't see (e.g. powered off, other subnet)
            foreach (var saved in Settings.Printers)
                if (!found.Any(f => f.Id == saved.Id || (f.Address != null && f.Address == saved.Address) || PrinterDevice.Similar(f.Name, saved.Name)))
                    found.Add(saved.ToDevice());

            Devices = found;
            AppLog.Write($"Discovery: {found.Count} printer(s): {string.Join(", ", found.Select(d => $"{d.Name} [{d.Connection}]"))}");
        }
        catch (Exception ex) { AppLog.Write("Discovery failed: " + ex); App.Window.Toast("Printer search failed: " + ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error); }
        finally { Discovering = false; }
        DevicesChanged?.Invoke();

        if (_earlySelect is { } early) { _earlySelect = null; try { await early; } catch { } }

        // re-select the previously used printer (or the only one)
        var keep = Current is null ? null : Devices.FirstOrDefault(d => d.Id == Current.Id || PrinterDevice.Similar(d.Name, Current.Name));
        var wanted = keep
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
        else await SelectAsync(wanted);
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
        await SelectAsync(dev);
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
