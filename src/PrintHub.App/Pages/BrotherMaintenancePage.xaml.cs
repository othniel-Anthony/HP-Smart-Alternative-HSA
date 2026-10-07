using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Printing;

namespace PrintHub.App.Pages;

/// <summary>
/// Brother-only tools over USB: ink levels, head cleaning, the print quality check sheet and the printer's waste counters.
/// The navigation entry only exists while a Brother printer is selected.
/// </summary>
public sealed partial class BrotherMaintenancePage : Page
{
    bool _busy;
    BrotherReport? _report;

    public BrotherMaintenancePage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged += OnChanged; Update(); _ = LoadReportAsync(); }
    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; }
    void OnChanged() => DispatcherQueue.TryEnqueue(() => { Update(); _ = LoadReportAsync(); });

    void Update()
    {
        var d = App.State.Current;
        Heading.Text = d is null ? "Brother maintenance" : $"Brother maintenance: {d.Name}";
        bool brother = d?.IsBrother == true;
        bool usb = brother && BrotherMaintenance.IsOnUsb(d!);

        Notice.IsOpen = true;
        if (!brother) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = "Choose a Brother printer"; Notice.Message = "These tools are for Brother printers."; }
        else if (!usb) { Notice.Severity = InfoBarSeverity.Warning; Notice.Title = "Printer not found on USB"; Notice.Message = "These tools work over the USB cable. Plug the printer in and switch it on."; }
        else Notice.IsOpen = false;

        bool can = usb && !_busy;
        CleanButton.IsEnabled = CheckButton.IsEnabled = RefreshButton.IsEnabled = ScopeCombo.IsEnabled = StrengthCombo.IsEnabled = can;
        CopyButton.IsEnabled = _report is not null;
        DriverButton.IsEnabled = QueueButton.IsEnabled = d?.SpoolerName is not null;
        if (!usb) ShowReport(null);
    }

    async Task LoadReportAsync()
    {
        var dev = App.State.Current;
        if (dev is null || !dev.IsBrother || _busy || !BrotherMaintenance.IsOnUsb(dev)) return;
        _busy = true; Update(); Ring.IsActive = true;
        try
        {
            var report = await BrotherMaintenance.ReadReportAsync(dev);
            ShowReport(report);
        }
        catch (Exception ex)
        {
            AppLog.Write("Brother report: " + ex.Message);
            ShowReport(null);
            InkNote.Text = "Could not read the printer: " + ex.Message;
        }
        finally { _busy = false; Ring.IsActive = false; Update(); }
    }

    static string N(long? n) => n is null ? "not reported" : n.Value.ToString("N0");

    void ShowReport(BrotherReport? r)
    {
        _report = r;
        InkPanel.Children.Clear();
        if (r is null)
        {
            AlignCounters.Text = PadCounters.Text = DetailsText.Text = "";
            CopyButton.IsEnabled = false;
            return;
        }
        foreach (var ink in r.Ink) InkPanel.Children.Add(Ui.SupplyRow(ink));
        var names = new[] { "Black", "Cyan", "Magenta", "Yellow" }.Select(c => r.CartridgeName(c)).Where(n => n is not null).Distinct().ToList();
        InkNote.Text = "Estimated by the printer, read over the USB cable." + (names.Count > 0 ? $" Cartridges: {string.Join(", ", names)}." : "");

        AlignCounters.Text = $"The printer has been asked to align its head {N(r.AlignmentTries)} time(s) and finished {N(r.AlignmentDone)}.";
        PadCounters.Text =
            $"Purges counted by the printer: {N(r.PurgeWasteCount)}\n" +
            $"Head wipes: {N(r.WipeCount)}\n" +
            $"Ink flushed (black): {N(r.FlushWasteBlack)}\n" +
            $"Ink flushed (colour): {N(r.FlushWasteColor)}\n" +
            $"Cleanings started by hand: {N(r.ManualPurges)}, by the printer itself: {N(r.AutoPurges)}";
        DetailsText.Text =
            $"Model: {r.Model ?? "?"}\nSerial number: {r.Serial ?? "?"}\nFirmware: {r.Firmware ?? "?"}\nPages printed: {N(r.TotalPages)}\nQuality check sheets printed: {N(r.QualityChecksPrinted)}";
        CopyButton.IsEnabled = true;
    }

    BrotherCleanScope Scope => Enum.Parse<BrotherCleanScope>((string)((ComboBoxItem)ScopeCombo.SelectedItem).Tag);
    BrotherCleanStrength Strength => Enum.Parse<BrotherCleanStrength>((string)((ComboBoxItem)StrengthCombo.SelectedItem).Tag);

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadReportAsync();

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var dev = App.State.Current; if (dev is null) return;
        var scope = Scope; var strength = Strength;
        string what = BrotherMaintenance.Describe(scope, strength);

        string warning = strength switch
        {
            BrotherCleanStrength.Normal => "Cleaning uses ink and takes about a minute or two. Print the quality check sheet first if you haven't; clean only if it shows gaps.",
            BrotherCleanStrength.Strong => "A strong cleaning uses much more ink than a normal one and fills the ink absorber faster. Use it only if a normal cleaning did not fix the print.",
            _ => "The strongest cleaning uses a lot of ink and fills the ink absorber much faster. Use it only after normal and strong cleanings did not fix the print.",
        };
        var low = _report is null ? new List<string>() : BrotherMaintenance.InksUsed(scope).Where(n => _report.Ink.FirstOrDefault(i => i.Name == n) is { IsLow: true }).ToList();
        if (low.Count > 0) warning += $"\n\nThe {string.Join(" and ", low).ToLowerInvariant()} ink is low: Brother warns that cleaning with little ink can damage the printer.";
        if (!await Ui.ConfirmAsync(XamlRoot, $"{what}?", warning, "Start")) return;

        await RunAsync(what, p => BrotherMaintenance.CleanAsync(dev, scope, strength, p));
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        var dev = App.State.Current; if (dev is null) return;
        if (!await Ui.ConfirmAsync(XamlRoot, "Print the quality check sheet?", "The printer prints one page with a colour pattern. Make sure paper is loaded.", "Print")) return;
        await RunAsync("Print quality check sheet", p => BrotherMaintenance.PrintQualityCheckAsync(dev, p));
    }

    async Task RunAsync(string title, Func<IProgress<string>, Task<BrotherResult>> work)
    {
        _busy = true; Update(); Ring.IsActive = true;
        try
        {
            var progress = new Progress<string>(s => ResultText.Text = s);
            var result = await work(progress);
            ResultText.Text = result.Summary;
            App.Window.Toast(result.Summary, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Brother maintenance ({title}): " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", ex.Message);
        }
        finally { _busy = false; Ring.IsActive = false; Update(); }
        await LoadReportAsync();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(_report.Raw);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        App.Window.Toast("The printer's report was copied to the clipboard.", InfoBarSeverity.Success);
    }

    void Driver_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenPreferences(App.State.Current!.SpoolerName!));
    void Queue_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenQueue(App.State.Current!.SpoolerName!));
    static void Safe(Action a) { try { a(); } catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); } }
}
