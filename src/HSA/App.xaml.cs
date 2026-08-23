using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using HSA.Services;
using HSA.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HSA;

public partial class App : Application
{
    // Per-user (Local\ scope) so multiple Windows users on the same machine can each
    // run their own copy. The "v1" suffix lets us bump the name on breaking changes.
    private const string SingleInstanceMutexName = @"Local\HSA.SingleInstance.v1";
    private const string ActivateEventName      = @"Local\HSA.Activate.v1";

    // CLI flags. Pass --new-instance to force a new process even if one is already
    // running (kills the existing one first). Useful if the previous instance was
    // killed abruptly (e.g. by Stop-Process -Force) and left the mutex orphaned.
    private const string ForceNewInstanceArg = "--new-instance";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateListener;

    private ServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services
        ?? throw new InvalidOperationException("Services not initialized.");

    public App()
    {
        // Catch WPF dispatcher-thread exceptions (XAML parse errors land here).
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception (WPF UI thread).");
            args.Handled = true;   // keep the process alive so the user can read the log
        };
        // Catch non-UI background exceptions.
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled AppDomain exception.");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Bootstrap Serilog early so single-instance and startup failures are logged
        // even before the rest of the app initializes.
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HSA", "Logs", "hsa-.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("HSA starting (PID {Pid}, args=[{Args}])",
            Environment.ProcessId, string.Join(' ', e.Args));

        // --- Single-instance enforcement ---
        var forceNew = e.Args.Any(a =>
            string.Equals(a, ForceNewInstanceArg, StringComparison.OrdinalIgnoreCase));

        if (forceNew)
            KillExistingInstancesAndWaitForMutexRelease();

        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            name: SingleInstanceMutexName, createdNew: out createdNew);

        if (!createdNew)
        {
            // Another instance is already running. Tell it to come to the foreground,
            // then exit cleanly so we don't show a second window.
            Log.Information("Another HSA instance is running; signaling it and exiting.");
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not signal the existing HSA instance to activate.");
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // We're the first instance — create the activation event and start a
        // background thread that listens for "another launch was attempted" signals.
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            name: ActivateEventName, out _);
        _activateListener = new Thread(ActivateListenerLoop)
        {
            IsBackground = true,
            Name = "HSA.ActivateListener"
        };
        _activateListener.Start();

        // --- DI setup ---
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.AddSerilog());
        sc.AddSingleton<IDialogService, DialogService>();
        sc.AddSingleton<IPrinterService, PrinterService>();
        sc.AddSingleton<DriverStoreManager>();
        sc.AddSingleton<WindowsUpdateClient>();
        sc.AddSingleton<DriverDownloader>();
        sc.AddSingleton<IDriverService, DriverService>();
        sc.AddSingleton<IFirmwareService, FirmwareService>();
        sc.AddSingleton<IppConsumableSource>();
        sc.AddSingleton<WsdPrintConsumableSource>();
        sc.AddSingleton<PrinterEndpointDiscovery>();
        sc.AddSingleton<EwsService>();
        sc.AddSingleton<EwsDiscoveryService>();
        sc.AddSingleton<IConsumableService, ConsumableService>();
        sc.AddSingleton<IModelImageService, ModelImageService>();

        // Settings + theme. SettingsService is constructed here (not via DI) so we can
        // read the persisted theme synchronously and install the right theme dictionary
        // BEFORE the first view is created. ThemeManager subscribes to SettingsService.Changed
        // and re-applies the dictionary on toggle.
        var settings = new SettingsService();
        var theme = new ThemeManager(settings);
        theme.Apply(settings.Current.ThemeMode);
        sc.AddSingleton(settings);
        sc.AddSingleton(theme);

        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<PrintersViewModel>();
        sc.AddSingleton<SuppliesViewModel>();
        sc.AddSingleton<DriversViewModel>();
        sc.AddSingleton<FirmwareViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        _services = sc.BuildServiceProvider();

        // --- Show main window ---
        var main = new MainWindow
        {
            DataContext = _services.GetRequiredService<MainViewModel>()
        };
        main.Show();
        Log.Information("Main window shown (theme={Theme}).", theme.CurrentMode);
    }

    /// <summary>
    /// Used when launched with --new-instance. Kills any running HSA processes and
    /// waits for the kernel to release the single-instance mutex they held, so the
    /// current process can acquire it on the next try.
    /// </summary>
    private static void KillExistingInstancesAndWaitForMutexRelease()
    {
        try
        {
            var procs = Process.GetProcessesByName("HSA");
            foreach (var p in procs)
            {
                if (p.Id == Environment.ProcessId)
                {
                    // Don't try to kill the process running this code.
                    p.Dispose();
                    continue;
                }
                try
                {
                    Log.Warning("--new-instance: killing existing HSA PID {Pid}", p.Id);
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not kill existing HSA PID {Pid}", p.Id);
                }
                finally { p.Dispose(); }
            }
            // Give the kernel a moment to reap the dead process and release the mutex.
            // 2s is overkill but predictable; the user's machine will only hit this code
            // path in a recovery scenario.
            Thread.Sleep(2000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KillExistingInstancesAndWaitForMutexRelease failed.");
        }
    }

    private void ActivateListenerLoop()
    {
        var handle = _activateEvent?.SafeWaitHandle;
        if (handle is null) return;
        while (true)
        {
            try
            {
                if (_activateEvent is null) return;
                if (!_activateEvent.WaitOne(500)) continue;

                // Bring the main window to the foreground. Marshalled via Dispatcher.
                Dispatcher.Invoke(() =>
                {
                    var w = MainWindow;
                    if (w is null) return;
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    w.Activate();
                    w.Topmost = true;
                    w.Topmost = false;
                    w.Focus();
                });
            }
            catch
            {
                // Listener thread must never crash.
                Thread.Sleep(200);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateListener?.Interrupt();
            _activateEvent?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { /* mutex may already be released */ }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
