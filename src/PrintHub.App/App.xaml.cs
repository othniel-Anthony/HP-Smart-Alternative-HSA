using Microsoft.UI.Xaml;
using PrintHub.App.Services;

namespace PrintHub.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public static AppState State { get; } = new();
    public static UpdateManager Updates { get; } = new();

    public App()
    {
        StartupTrace.Mark("App constructor");
        // "HSA.exe --prewarm": used by the installer. By the time this code runs the single-file exe has already unpacked itself,
        // so exiting here means the first real launch starts from the unpacked copy instead of unpacking ~160 MB.
        if (Environment.GetCommandLineArgs().Contains("--prewarm")) Environment.Exit(0);
        // "HSA.exe --apply-update <target> <pid>" (finishing an update) and "HSA.exe --uninstall": no window, then exit
        if (HeadlessModes.RunIfRequested(Environment.GetCommandLineArgs())) Environment.Exit(0);
        Core.Diag.Sink = AppLog.Write;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Write("Unhandled exception: " + e.Exception);
            e.Handled = true;
            Window?.Toast("Something went wrong: " + e.Exception.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Write("Task: " + e.Exception); e.SetObserved(); };
    }

    /// <summary>Files passed with <c>--import</c> (opened as pages in the Scan editor).</summary>
    public static List<string> PendingImports { get; } = new();
    /// <summary>Files passed with <c>--print</c> (queued on the Print page).</summary>
    public static List<string> PendingPrints { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTrace.Mark("OnLaunched");
        Window = new MainWindow();
        StartupTrace.Mark("MainWindow created");
        Window.Activate();
        StartupTrace.Mark("Window activated");
        Core.Discovery.PrinterDiscovery.Trace = s => StartupTrace.Mark("  discovery: " + s);
        _ = State.InitializeAsync();
        _ = Task.Run(() => PrintHub.Core.Updates.UpdateApplier.CleanUp(UpdateManager.Current, Environment.ProcessPath));

        // PrintHub.exe [--page home|print|scan|copy|shortcuts|printer|web|settings] [--import file ...]
        var cli = Environment.GetCommandLineArgs().Skip(1).ToList();
        for (int i = 0; i < cli.Count; i++)
        {
            if (cli[i] == "--page" && i + 1 < cli.Count) Window.NavigateTo(cli[++i]);
            else if (cli[i] == "--import" && i + 1 < cli.Count) { PendingImports.Add(cli[++i]); Window.NavigateTo("scan"); }
            else if (cli[i] == "--print" && i + 1 < cli.Count) { PendingPrints.Add(cli[++i]); Window.NavigateTo("print"); }
        }
    }
}
