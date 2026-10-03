using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Printing;
using Windows.ApplicationModel.DataTransfer;

namespace PrintHub.App.Pages;

public sealed partial class PrintPage : Page
{
    static readonly List<string> Files = new();           // survives page navigation within a session
    static PrintOptions Remembered = new();

    PrintSource? _preview;
    int _previewPage;
    int _previewToken;

    public PrintPage()
    {
        InitializeComponent();
        Ui.Fill(ColorCombo, new[] { ("Colour", true), ("Black and white", false) }, Remembered.Color);
        Ui.Fill(DuplexCombo, new[] { ("Off (one side)", DuplexMode.Off), ("Flip on long edge", DuplexMode.LongEdge), ("Flip on short edge", DuplexMode.ShortEdge) }, Remembered.Duplex);
        Ui.Fill(PaperCombo, PaperChoice.All.Select(p => (p.Name, p)), Remembered.Paper);
        Ui.Fill(ScaleCombo, new[] { ("Fit to page", ScaleMode.FitToPage), ("Fill page (crop edges)", ScaleMode.FillPage), ("Actual size", ScaleMode.ActualSize), ("Photo size", ScaleMode.PhotoSize) }, Remembered.Scale);
        Ui.Fill(PhotoSizeCombo, new[] { ("4 × 6 in", (4.0, 6.0)), ("5 × 7 in", (5.0, 7.0)), ("3.5 × 5 in", (3.5, 5.0)), ("8 × 10 in", (8.0, 10.0)), ("Wallet 2.5 × 3.5 in", (2.5, 3.5)) }, (Remembered.PhotoWidthIn, Remembered.PhotoHeightIn));
        Ui.Fill(QualityCombo, new[] { ("Draft (saves ink)", PrintQuality.Draft), ("Normal", PrintQuality.Normal), ("Best", PrintQuality.Best) }, Remembered.Quality);
        Ui.Fill(RouteCombo, new[] { ("Automatic", PrintRoute.Auto), ("Windows printer driver", PrintRoute.WindowsDriver), ("Directly (IPP, no driver)", PrintRoute.DirectIpp) }, App.State.Settings.PrintRoute);
        BorderlessCheck.IsChecked = Remembered.Borderless;
    }

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged;
        if (App.PendingPrints.Count > 0) { var queued = App.PendingPrints.ToList(); App.PendingPrints.Clear(); AddFiles(queued); }
        RebuildList();
        UpdateRouteNote();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged -= OnChanged;
        Remembered = ReadOptions();
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(UpdateRouteNote);

    void UpdateRouteNote()
    {
        var d = App.State.Current;
        PrintButton.IsEnabled = d is not null;
        RouteNote.Text = d is null ? "Choose a printer first."
            : d.SpoolerName is not null && App.State.Session?.Ipp is not null ? "Both a Windows driver and a direct connection are available. Automatic uses the driver."
            : d.SpoolerName is not null ? "Printing with the Windows driver for this printer."
            : App.State.Session?.Ipp is not null ? "No Windows driver is installed, so HSA prints directly to the printer (works for most modern printers, including over USB)."
            : "This printer can't print yet: it has no Windows driver and no direct connection.";
    }

    PrintOptions ReadOptions()
    {
        var (pw, ph) = Ui.Selected<(double, double)>(PhotoSizeCombo);
        return new PrintOptions
        {
            Copies = (int)Math.Max(1, CopiesBox.Value),
            Color = Ui.Selected<bool>(ColorCombo),
            Duplex = Ui.Selected<DuplexMode>(DuplexCombo),
            Paper = Ui.Selected<PaperChoice>(PaperCombo) ?? PaperChoice.Default,
            Scale = Ui.Selected<ScaleMode>(ScaleCombo),
            PhotoWidthIn = pw == 0 ? 4 : pw, PhotoHeightIn = ph == 0 ? 6 : ph,
            Quality = Ui.Selected<PrintQuality>(QualityCombo),
            PageRange = string.IsNullOrWhiteSpace(PagesBox.Text) ? null : PagesBox.Text.Trim(),
            Borderless = BorderlessCheck.IsChecked == true,
            Route = Ui.Selected<PrintRoute>(RouteCombo),
        };
    }

    void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PhotoSizeCombo.Visibility = Ui.Selected<ScaleMode>(ScaleCombo) == ScaleMode.PhotoSize ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------ file list

    void RebuildList()
    {
        FileList.Items.Clear();
        foreach (var f in Files) FileList.Items.Add(CreateRow(f));
        ClearButton.IsEnabled = Files.Count > 0;
        if (Files.Count > 0 && FileList.SelectedIndex < 0) FileList.SelectedIndex = 0;
        else if (Files.Count == 0) { PreviewImage.Source = null; PreviewHint.Visibility = Visibility.Visible; PageNav.Visibility = Visibility.Collapsed; }
    }

    FrameworkElement CreateRow(string path)
    {
        var g = new Grid { ColumnSpacing = 8, Tag = path };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var rm = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6), Background = null, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(rm, "Remove " + Path.GetFileName(path));
        rm.Click += (_, _) => { Files.Remove(path); RebuildList(); };
        Grid.SetColumn(rm, 1); g.Children.Add(rm);
        return g;
    }

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picked = await Ui.PickFilesAsync(".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".txt", ".docx", ".doc", ".xlsx", ".pptx", ".rtf");
        AddFiles(picked);
    }

    void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths) if (File.Exists(p) && !Files.Contains(p)) Files.Add(p);
        RebuildList();
    }

    void Clear_Click(object sender, RoutedEventArgs e) { Files.Clear(); RebuildList(); }

    void Drop_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to print list";
    }

    async void Drop_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        AddFiles(items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path));
    }

    // ------------------------------------------------------------ preview

    async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not Grid { Tag: string path }) return;
        int token = ++_previewToken;
        PreviewHint.Text = "Loading preview…"; PreviewHint.Visibility = Visibility.Visible; PreviewImage.Source = null; PageNav.Visibility = Visibility.Collapsed;
        try
        {
            var src = await PrintSource.FromFileAsync(path);
            if (token != _previewToken) return;
            _preview = src; _previewPage = 0;
            await ShowPreviewPageAsync(token);
        }
        catch (NotSupportedException)
        {
            if (token == _previewToken) { _preview = null; PreviewHint.Text = "No preview for this file type. It will be sent to the app that opens it."; }
        }
        catch (Exception ex) { if (token == _previewToken) { _preview = null; PreviewHint.Text = "Can't preview this file: " + ex.Message; } }
    }

    async Task ShowPreviewPageAsync(int token)
    {
        if (_preview is null) return;
        var bytes = _preview.GetPageBytes(_previewPage);
        var img = await Ui.ToBitmapImageAsync(bytes, 900);
        if (token != _previewToken) return;
        PreviewImage.Source = img; PreviewHint.Visibility = Visibility.Collapsed;
        PageNav.Visibility = _preview.PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Text = $"Page {_previewPage + 1} of {_preview.PageCount}";
    }

    async void PrevPage_Click(object sender, RoutedEventArgs e) { if (_preview is not null && _previewPage > 0) { _previewPage--; await ShowPreviewPageAsync(_previewToken); } }
    async void NextPage_Click(object sender, RoutedEventArgs e) { if (_preview is not null && _previewPage < _preview.PageCount - 1) { _previewPage++; await ShowPreviewPageAsync(_previewToken); } }

    // ------------------------------------------------------------ printing

    async void Print_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State; var dev = s.Current;
        if (dev is null) return;
        if (Files.Count == 0)
        {
            App.Window.Toast("Add a document or photo to print first.", InfoBarSeverity.Warning);
            return;
        }

        var opts = ReadOptions();
        PrintButton.IsEnabled = false; Progress.Visibility = Visibility.Visible;
        int ok = 0, failed = 0;
        try
        {
            foreach (var path in Files.ToList())
            {
                StatusText.Text = $"Sending {Path.GetFileName(path)}…";
                try
                {
                    if (PrintSource.IsPdf(path) || PrintSource.IsImage(path))
                        await PrintService.PrintFileAsync(dev, s.Session, path, opts);
                    else
                        await PrintViaOwnerAppAsync(dev, path);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Write($"Print {path}: {ex}");
                    await Ui.MessageAsync(XamlRoot, $"Couldn't print {Path.GetFileName(path)}", ex.Message);
                }
            }
        }
        finally { Progress.Visibility = Visibility.Collapsed; PrintButton.IsEnabled = true; }

        StatusText.Text = failed == 0 ? $"Sent {ok} file{(ok == 1 ? "" : "s")} to {dev.Name}." : $"{ok} sent, {failed} failed.";
        if (ok > 0) App.Window.Toast(StatusText.Text, failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        _ = s.RefreshStatusAsync();
    }

    /// <summary>For formats we can't render (Word, text…): ask the registered app to print to the selected Windows queue.</summary>
    async Task PrintViaOwnerAppAsync(Core.Discovery.PrinterDevice dev, string path)
    {
        if (dev.SpoolerName is null)
            throw new NotSupportedException($"'{Path.GetExtension(path)}' files need the app that owns them to print, which requires a Windows printer driver for {dev.Name}. Export the file as PDF and print that instead.");
        if (!await Ui.ConfirmAsync(XamlRoot, "Print with another app",
                $"HSA can't draw {Path.GetExtension(path)} files itself. Windows will ask the app that normally opens them to print this file on {dev.Name}, using that app's own print settings.", "Print", "Skip"))
            return;
        var psi = new System.Diagnostics.ProcessStartInfo(path) { Verb = "printto", Arguments = $"\"{dev.SpoolerName}\"", UseShellExecute = true, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        try { System.Diagnostics.Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new NotSupportedException("No app on this PC can print this file type directly. Open it in its own app and print from there, or export it to PDF.");
        }
    }
}
