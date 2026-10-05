using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Printing;

namespace PrintHub.App.Pages;

/// <summary>Epson-only tools: nozzle check, head cleaning, printer reset and waste-ink counters. The navigation entry only exists while an Epson printer is selected.</summary>
public sealed partial class MaintenancePage : Page
{
    bool _busy;
    EpsonDatabase? _db;
    string? _dbError;
    EpsonAnalysis? _analysis;

    public MaintenancePage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged += OnChanged; LoadDatabase(); Update(); }
    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; }
    void OnChanged() => DispatcherQueue.TryEnqueue(() => { _analysis = null; CountersPanel.Children.Clear(); CountersNote.Text = ""; Update(); });

    void LoadDatabase() => _db = EpsonCounterService.TryLoadDatabase(out _dbError);

    void Update()
    {
        var d = App.State.Current;
        Heading.Text = d is null ? "Epson maintenance" : $"Maintenance: {d.Name}";
        bool epson = d?.IsEpson == true;
        bool usb = epson && EpsonMaintenance.FindPrintInterface(d!) is not null;

        Notice.IsOpen = true;
        if (!epson) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = "Choose an Epson printer"; Notice.Message = "These tools are for Epson printers."; }
        else if (!usb) { Notice.Severity = InfoBarSeverity.Warning; Notice.Title = "Printer not found on USB"; Notice.Message = "Cleaning, reset and the counters work over the USB cable. Plug the printer in and switch it on (it can stay on your network too), or use the Epson driver settings below."; }
        else Notice.IsOpen = false;

        bool can = usb && !_busy;
        foreach (var b in new[] { NozzleButton, CleanButton, CleanLevel2Button, CleanLevel3Button, CleanBlackButton, CleanColourButton, ResetButton }) b.IsEnabled = can;
        bool win = d?.SpoolerName is not null;
        DriverButton.IsEnabled = QueueButton.IsEnabled = win;

        // waste ink counters
        DbButton.IsEnabled = !_busy;
        CheckButton.IsEnabled = can && _db is not null;
        CountersResetButton.IsEnabled = can && _db is not null;
        UndoButton.IsEnabled = can && _db is not null && _analysis?.Spec is { } sp && EpsonCounterService.LatestBackup(sp.Key) is not null;
        DbText.Text = _db is not null ? $"Model database loaded automatically: {_db.ModelCount} models ({_db.SourcePath})." :
            _dbError is not null ? $"The model database could not be read: {_dbError}" :
            "No model database is loaded. Put epson-database.json in HSA's data folder (or next to HSA.exe) and it is picked up automatically, or choose the file below. HSA does not include one.";
    }

    async Task RunAsync(string startText, Func<CancellationToken, Task<string>> work, bool checkLinkFirst = true, TimeSpan? timeout = null)
    {
        var dev = App.State.Current; if (dev is null) return;
        _busy = true; Update(); Ring.IsActive = true; ResultText.Text = startText;
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(6));
            // A reply from the printer proves the USB link works before anything is sent that uses ink.
            // (Reset skips this: a printer that is stuck may not answer, and that is exactly when you want to reset it.)
            var status = checkLinkFirst ? await EpsonMaintenance.QueryStatusAsync(dev, cts.Token) : "";
            if (status is null) throw new InvalidOperationException("The printer did not answer on its USB port. Check that it is on and not busy, then try again.");
            ResultText.Text = await work(cts.Token);
            App.Window.Toast(ResultText.Text, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { ResultText.Text = "The printer took too long to answer."; }
        catch (Exception ex)
        {
            AppLog.Write("Epson maintenance: " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", ex.Message);
        }
        finally { _busy = false; Ring.IsActive = false; Update(); }
    }

    async void Nozzle_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Printing the nozzle check. Please wait until the page is out…", async ct =>
        {
            await EpsonMaintenance.NozzleCheckAsync(App.State.Current!, ct);
            return "Nozzle check finished. Hold the page up to the light: every line should be complete.";
        });

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        bool isLevel = int.TryParse(tag, out var level);
        var which = isLevel ? EpsonCleaning.All : Enum.Parse<EpsonCleaning>(tag);
        int cycles = isLevel ? level : 1;
        string what = isLevel ? $"Level {level} cleaning ({cycles} cycle{(cycles == 1 ? "" : "s")}, all nozzles)" : which == EpsonCleaning.Black ? "Black-only cleaning" : "Colour-only cleaning";

        bool ok = await Ui.ConfirmAsync(XamlRoot, $"{what}?",
            "Cleaning uses ink" + (cycles > 1 ? $" ({cycles} times as much as level 1)" : "") + " and takes " + (cycles > 1 ? $"about {cycles} to {cycles * 2} minutes" : "a minute or two") +
            ". Print a nozzle check first if you haven't; clean only if it shows gaps. Don't switch the printer off while it cleans.", "Clean");
        if (!ok) return;
        bool check = CheckAfter.IsChecked == true;
        await RunAsync($"{what}. Don't switch the printer off…", async ct =>
        {
            var progress = new Progress<string>(t => ResultText.Text = t);
            await EpsonMaintenance.CleanHeadAsync(App.State.Current!, which, cycles, check, progress, ct);
            return check ? "Cleaning finished. A nozzle check was printed: hold it up to the light to see the result." : "Cleaning finished. Print a nozzle check to see the result.";
        }, timeout: TimeSpan.FromMinutes(14));
    }

    async void Reset_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await Ui.ConfirmAsync(XamlRoot, "Reset the printer?",
            "Jobs waiting in Windows for this printer will be cancelled, and the printer will be sent a reset. Settings and counters are not touched.", "Reset");
        if (!ok) return;
        await RunAsync("Resetting…", async ct =>
        {
            var summary = await EpsonMaintenance.ResetAsync(App.State.Current!, ct);
            _ = App.State.SelectAsync(App.State.Current); // reconnect
            return char.ToUpper(summary[0]) + summary[1..] + ". Reconnecting.";
        }, checkLinkFirst: false);
    }

    // ---------------------------------------------------------------- waste ink counters

    async void Db_Click(object sender, RoutedEventArgs e)
    {
        var picked = (await Ui.PickFilesAsync(".json")).FirstOrDefault();
        if (picked is null) return;
        try
        {
            var db = EpsonDatabase.Load(picked); // refuse a wrong file before it replaces anything
            Directory.CreateDirectory(Path.GetDirectoryName(EpsonCounterService.DatabasePath)!);
            File.Copy(picked, EpsonCounterService.DatabasePath, overwrite: true);
            _db = db; _dbError = null; _analysis = null;
            CountersPanel.Children.Clear(); CountersNote.Text = "";
            App.Window.Toast($"Model database loaded ({db.ModelCount} models).", InfoBarSeverity.Success);
        }
        catch (Exception ex) { await Ui.MessageAsync(XamlRoot, "That file can't be used", ex.Message); }
        Update();
    }

    async Task<EpsonAnalysis?> AnalyzeAsync()
    {
        var dev = App.State.Current;
        if (dev is null || _db is null) return null;
        _busy = true; Update(); Ring.IsActive = true; ResultText.Text = "Reading the counters…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _analysis = await EpsonCounterService.AnalyzeAsync(dev, _db, cts.Token);
            ShowAnalysis(_analysis);
            ResultText.Text = "";
            return _analysis;
        }
        catch (OperationCanceledException) { ResultText.Text = "The printer took too long to answer."; return null; }
        catch (Exception ex)
        {
            AppLog.Write("Epson counters: " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", ex.Message);
            return null;
        }
        finally { _busy = false; Ring.IsActive = false; Update(); }
    }

    void ShowAnalysis(EpsonAnalysis a)
    {
        CountersPanel.Children.Clear();
        if (a.Problem is not null)
        {
            CountersNote.Text = a.Problem + (a.ReportedModel is null ? "" : $"  (The printer reports itself as “{a.ReportedModel}”.)");
            return;
        }
        foreach (var c in a.Counters) CountersPanel.Children.Add(CounterRow(c));
        CountersNote.Text = $"Model: {a.ModelKey}" + (a.ReportedModel is not null ? $" (the printer reports “{a.ReportedModel}”)" : "") + ". " +
            (a.NothingToChange ? "The counters are already at their reset values." : $"A reset would change {a.Plan.Count} memory cell{(a.Plan.Count == 1 ? "" : "s")}.");
    }

    static FrameworkElement CounterRow(EpsonCounterReading c)
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.Children.Add(new TextBlock { Text = c.Description, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (c.Max > 0)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Min(100, c.Percent), Height = 10, VerticalAlignment = VerticalAlignment.Center };
            if (c.Percent >= 100) bar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            Grid.SetColumn(bar, 1); grid.Children.Add(bar);
            var pct = new TextBlock { Text = c.Percent >= 100 ? "100% (limit reached)" : $"{c.Percent}% used", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pct, 2); grid.Children.Add(pct);
        }
        else
        {
            var n = new TextBlock { Text = c.Value == 0 ? "at reset values" : $"{c.Value} cell(s) differ", Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(n, 1); grid.Children.Add(n);
        }
        return grid;
    }

    async void Check_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    async void CountersReset_Click(object sender, RoutedEventArgs e)
    {
        var dev = App.State.Current; if (dev is null || _db is null) return;
        var a = await AnalyzeAsync();
        if (a is null) return;
        if (!a.CanReset) { await Ui.MessageAsync(XamlRoot, "Can't reset this printer", a.Problem ?? "Unknown problem."); return; }
        if (a.NothingToChange) { await Ui.MessageAsync(XamlRoot, "Nothing to reset", "The counters are already at their reset values."); return; }

        var check = new CheckBox { Content = "I have cleaned or replaced the waste ink pads, and nothing is printing." };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"This writes {a.Plan.Count} value{(a.Plan.Count == 1 ? "" : "s")} into the memory of {dev.Name} (model {a.ModelKey}) to clear its waste-ink counters. " +
                   "HSA saves a backup of those values first, checks each one after writing it, and puts everything back if the printer refuses one. " +
                   "Don't unplug or switch off the printer until it says it's done.",
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
            Text = "A reset only clears the counter. It does not empty the pads: if they are full, ink can leak from the printer and damage what is underneath it.",
        });
        panel.Children.Add(check);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Reset the waste ink counters?", Content = panel,
            PrimaryButtonText = "Reset counters", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
        };
        check.Checked += (_, _) => dlg.IsPrimaryButtonEnabled = true;
        check.Unchecked += (_, _) => dlg.IsPrimaryButtonEnabled = false;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        await RunCounterJobAsync("Resetting the counters…", (progress, ct) => EpsonCounterService.ResetAsync(dev, _db, progress, ct), "Counter reset");
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var dev = App.State.Current; if (dev is null || _db is null) return;
        bool ok = await Ui.ConfirmAsync(XamlRoot, "Put the old counter values back?",
            "This writes the values saved by the last reset back into the printer, so its counters show what they did before.", "Restore");
        if (!ok) return;
        await RunCounterJobAsync("Restoring…", (progress, ct) => EpsonCounterService.RestoreLatestAsync(dev, _db, progress, ct), "Restore");
    }

    async Task RunCounterJobAsync(string startText, Func<IProgress<string>, CancellationToken, Task<EpsonResetResult>> job, string title)
    {
        _busy = true; Update(); Ring.IsActive = true; ResultText.Text = startText;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var progress = new Progress<string>(t => ResultText.Text = t);
            var r = await job(progress, cts.Token);
            ResultText.Text = r.Summary;
            AppLog.Write($"Epson {title}: success={r.Success} {r.Summary} backup={r.BackupPath}");
            await Ui.MessageAsync(XamlRoot, r.Success ? title + " done" : title + " did not finish",
                r.Summary + (r.BackupPath is null ? "" : $"\n\nBackup of the old values:\n{r.BackupPath}"));
        }
        catch (OperationCanceledException) { ResultText.Text = "The printer took too long to answer."; }
        catch (Exception ex)
        {
            AppLog.Write("Epson counter job: " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", ex.Message);
        }
        finally { _busy = false; Ring.IsActive = false; _analysis = null; Update(); }
        if (_db is not null) _ = AnalyzeAsync(); // show the counters as they are now
    }

    void Driver_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenPreferences(App.State.Current!.SpoolerName!));
    void Queue_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenQueue(App.State.Current!.SpoolerName!));
    static void Safe(Action a) { try { a(); } catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); } }
}
