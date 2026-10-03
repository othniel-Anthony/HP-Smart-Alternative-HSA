using Microsoft.UI.Xaml;
using PrintHub.App.Services;

namespace PrintHub.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public static AppState State { get; } = new();

    public App()
    {
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
        Window = new MainWindow();
        Window.Activate();
        _ = State.InitializeAsync();

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
