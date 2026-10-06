using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Printing;

namespace PrintHub.App.Pages;

/// <summary>Canon-only tools: head cleaning, automatic head alignment and quality pages over USB. The navigation entry only exists while a Canon printer is selected.</summary>
public sealed partial class CanonMaintenancePage : Page
{
    bool _busy;

    public CanonMaintenancePage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged += OnChanged; Update(); }
    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; }
    void OnChanged() => DispatcherQueue.TryEnqueue(Update);

    void Update()
    {
        var d = App.State.Current;
        Heading.Text = d is null ? "Canon maintenance" : $"Canon maintenance: {d.Name}";
        bool canon = d?.IsCanon == true;
        bool usb = canon && CanonMaintenance.IsOnUsb(d!);

        Notice.IsOpen = true;
        if (!canon) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = "Choose a Canon printer"; Notice.Message = "These tools are for Canon printers."; }
        else if (!usb) { Notice.Severity = InfoBarSeverity.Warning; Notice.Title = "Printer not found on USB"; Notice.Message = "These tools work over the USB cable. Plug the printer in and switch it on."; }
        else Notice.IsOpen = false;

        bool can = usb && !_busy;
        CleanMenuButton.IsEnabled = ReportMenuButton.IsEnabled = AlignButton.IsEnabled = can;
        DriverButton.IsEnabled = QueueButton.IsEnabled = d?.SpoolerName is not null;
    }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        var dev = App.State.Current; if (dev is null) return;
        var action = Enum.Parse<CanonAction>((string)((FrameworkElement)sender).Tag);
        string title = CanonMaintenance.Title(action);

        string? warning = action switch
        {
            CanonAction.AutoAlign => "The printer prints an alignment sheet. On a printer with a scanner, put the sheet face down on the glass and follow the printer's own prompt (press its OK / Start button). Make sure plain paper is loaded. Printers without a scanner usually do this from Canon's driver instead.",
            CanonAction.Clean or CanonAction.CleanBlack or CanonAction.CleanColour => "Cleaning uses ink and takes about a minute. Print a nozzle check first if you haven't; clean only if it shows gaps.",
            CanonAction.CleanDeep or CanonAction.CleanBlackDeep or CanonAction.CleanColourDeep => "Deep cleaning uses more ink than normal cleaning and takes a couple of minutes. Use it only if normal cleaning did not fix missing lines.",
            _ => null,
        };
        if (warning is not null && !await Ui.ConfirmAsync(XamlRoot, $"{title}?", warning, "Start")) return;

        _busy = true; Update(); Ring.IsActive = true;
        try
        {
            await CanonMaintenance.SendAsync(dev, action);
            var wait = CanonMaintenance.TypicalDuration(action);
            // Canon printers report no status over this channel, so wait about as long as the job takes before allowing the next one
            for (var left = (int)wait.TotalSeconds; left > 0; left--)
            {
                ResultText.Text = $"{title}: sent. The printer is working (about {left} s). Don't switch it off.";
                await Task.Delay(1000);
                if (!IsLoaded) return;
            }
            ResultText.Text = action is CanonAction.NozzleCheck or CanonAction.AlignmentCheck
                ? $"{title}: it should be out now. If nothing printed, check paper and the printer's lights."
                : $"{title}: the printer should be finished. Print a nozzle check to see the result.";
            App.Window.Toast(ResultText.Text, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Write("Canon maintenance: " + ex);
            ResultText.Text = "";
            await Ui.MessageAsync(XamlRoot, "That didn't work", ex.Message);
        }
        finally { _busy = false; Ring.IsActive = false; Update(); }
    }

    void Driver_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenPreferences(App.State.Current!.SpoolerName!));
    void Queue_Click(object sender, RoutedEventArgs e) => Safe(() => SpoolerPrinters.OpenQueue(App.State.Current!.SpoolerName!));
    static void Safe(Action a) { try { a(); } catch (Exception ex) { App.Window.Toast(ex.Message, InfoBarSeverity.Warning); } }
}
