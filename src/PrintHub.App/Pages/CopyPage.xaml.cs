using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Escl;
using PrintHub.Core.Printing;

namespace PrintHub.App.Pages;

public sealed partial class CopyPage : Page
{
    CancellationTokenSource? _cts;

    public CopyPage()
    {
        InitializeComponent();
        Ui.Fill(ColorCombo, new[] { ("Colour", true), ("Black and white", false) }, true);
        Ui.Fill(SourceCombo, new[] { ("Scanner glass", ScanSource.Flatbed), ("Document feeder", ScanSource.Feeder) }, ScanSource.Flatbed);
        Ui.Fill(QualityCombo, new[] { ("Draft", PrintQuality.Draft), ("Normal", PrintQuality.Normal), ("Best", PrintQuality.Best) }, PrintQuality.Normal);
        Ui.Fill(PaperCombo, PaperChoice.All.Select(p => (p.Name, p)), PaperChoice.Default);
        Ui.Fill(ScaleCombo, new[] { ("Fit to page", ScaleMode.FitToPage), ("Original size", ScaleMode.ActualSize), ("Fill page", ScaleMode.FillPage) }, ScaleMode.FitToPage);
        Ui.Fill(DuplexCombo, new[] { ("Off", DuplexMode.Off), ("Flip on long edge", DuplexMode.LongEdge), ("Flip on short edge", DuplexMode.ShortEdge) }, DuplexMode.Off);
    }

    void Page_Loaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged += OnChanged; Update(); }
    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; }
    void OnChanged() => DispatcherQueue.TryEnqueue(Update);

    void Update()
    {
        var d = App.State.Current;
        Notice.IsOpen = true;
        if (d is null) Notice.Message = "Choose a printer first.";
        else if (!d.CanScan && !d.CanPrint) Notice.Message = $"{d.Name} can't print or scan from here yet.";
        else if (!d.CanScan) Notice.Message = $"{d.Name} has no scanner that HSA can reach, so it can't make copies.";
        else if (!d.CanPrint) Notice.Message = $"{d.Name} can't print from here, so it can't make copies.";
        else Notice.IsOpen = false;
        StartButton.IsEnabled = d is { CanScan: true, CanPrint: true } && _cts is null;
    }

    void IdCheck_Changed(object sender, RoutedEventArgs e) => IdNote.Visibility = IdCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State; var dev = s.Current; if (dev is null) return;
        var settings = new CopySettings
        {
            Copies = (int)Math.Max(1, CopiesBox.Value),
            Color = Ui.Selected<bool>(ColorCombo),
            Source = Ui.Selected<ScanSource>(SourceCombo),
            Quality = Ui.Selected<PrintQuality>(QualityCombo),
            Paper = Ui.Selected<PaperChoice>(PaperCombo) ?? PaperChoice.Default,
            Scale = Ui.Selected<ScaleMode>(ScaleCombo),
            Duplex = Ui.Selected<DuplexMode>(DuplexCombo),
            Enhance = EnhanceCheck.IsChecked == true,
            IdCard = IdCheck.IsChecked == true,
            Route = s.Settings.PrintRoute,
        };

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible; Ring.IsActive = true;
        var progress = new Progress<string>(t => StatusText.Text = t);
        try
        {
            await CopyService.CopyAsync(dev, s.Session, settings,
                promptNextSide: async () => await Ui.MessageAsync(XamlRoot, "Flip the card", "Turn the card over, place the back on the scanner glass, then press OK."),
                progress, _cts.Token);
            App.Window.Toast("Copy sent to the printer.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (Exception ex)
        {
            AppLog.Write("Copy: " + ex);
            StatusText.Text = "";
            await Ui.MessageAsync(XamlRoot, "Copy failed", ex.Message);
        }
        finally
        {
            _cts.Dispose(); _cts = null;
            Ring.IsActive = false; CancelButton.Visibility = Visibility.Collapsed; Update();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); StatusText.Text = "Cancelling…"; }
}
