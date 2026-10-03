using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using PrintHub.App.Services;
using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;
using PrintHub.Core.Printing;
using PrintHub.Core.Scanning;

namespace PrintHub.App.Pages;

public sealed class PageVm : INotifyPropertyChanged
{
    public ScannedPage Page { get; }
    public BitmapImage? Thumb { get; private set; }
    public string Label { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public PageVm(ScannedPage page) => Page = page;

    public void Update(BitmapImage thumb, int number)
    {
        Thumb = thumb; Label = $"Page {number}" + (Page.Edits.IsIdentity ? "" : " ✎");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }
}

public sealed partial class ScanPage : Page
{
    // pages survive navigation to other tabs
    static readonly ObservableCollection<PageVm> Session = new();
    static string? _lastSaved;
    public ObservableCollection<PageVm> Pages => Session;

    CancellationTokenSource? _cts;
    EsclCapabilities? _caps;
    int _previewToken, _adjustToken;
    bool _loading;

    PageVm? Current => Strip.SelectedItem as PageVm;

    public ScanPage()
    {
        InitializeComponent();
        _loading = true;
        Ui.Fill(PresetCombo, ScanPreset.Defaults.Select(p => ($"{p.Name}  -  {p.Description}", p)), ScanPreset.Defaults[0]);
        Ui.Fill(SourceCombo, new[] { ("Scanner glass (flatbed)", ScanSource.Flatbed), ("Document feeder", ScanSource.Feeder), ("Document feeder, both sides", ScanSource.FeederDuplex) }, ScanSource.Flatbed);
        Ui.Fill(ColorCombo, new[] { ("Colour", ScanColor.Color), ("Grayscale", ScanColor.Grayscale), ("Black and white", ScanColor.BlackAndWhite) }, ScanColor.Color);
        Ui.Fill(DpiCombo, new[] { 75, 100, 150, 200, 300, 600, 1200 }.Select(d => ($"{d}", d)), 300);
        Ui.Fill(PaperCombo, PaperSize.All.Select(p => (p.Name, p)), PaperSize.Auto);
        Ui.Fill(FormatCombo, new[] { ("PDF", OutputFormat.Pdf), ("JPEG", OutputFormat.Jpeg), ("PNG", OutputFormat.Png), ("TIFF", OutputFormat.Tiff) }, OutputFormat.Pdf);
        _loading = false;
        ApplyPreset(ScanPreset.Defaults[0]);
    }

    async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged;
        UpdateAvailability();
        UpdateButtons();
        if (Pages.Count > 0 && Strip.SelectedItem is null) Strip.SelectedIndex = 0;
        if (App.PendingImports.Count > 0)
        {
            var files = App.PendingImports.ToList(); App.PendingImports.Clear();
            await ImportFilesAsync(files);
        }
        await LoadCapabilitiesAsync();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; }

    void OnChanged() => DispatcherQueue.TryEnqueue(async () => { UpdateAvailability(); await LoadCapabilitiesAsync(); });

    // ============================================================ settings

    void ApplyPreset(ScanPreset p)
    {
        _loading = true;
        var s = p.Settings;
        Ui.Select(SourceCombo, s.Source); Ui.Select(ColorCombo, s.Color); Ui.Select(DpiCombo, s.Dpi);
        Ui.Select(PaperCombo, s.Paper); Ui.Select(FormatCombo, s.Format);
        AutoCropCheck.IsChecked = s.AutoCrop; StraightenCheck.IsChecked = s.AutoStraighten; EnhanceCheck.IsChecked = s.Enhance;
        OcrCheck.IsChecked = s.Ocr || (s.Format == OutputFormat.Pdf && App.State.Settings.SearchablePdf);
        _loading = false;
    }

    void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Ui.Selected<ScanPreset>(PresetCombo) is { } p) ApplyPreset(p);
    }

    ScanSettings ReadSettings() => new()
    {
        Source = Ui.Selected<ScanSource>(SourceCombo), Color = Ui.Selected<ScanColor>(ColorCombo), Dpi = Ui.Selected<int>(DpiCombo) is var d && d > 0 ? d : 300,
        Paper = Ui.Selected<PaperSize>(PaperCombo) ?? PaperSize.Auto, Format = Ui.Selected<OutputFormat>(FormatCombo),
        AutoCrop = AutoCropCheck.IsChecked == true, AutoStraighten = StraightenCheck.IsChecked == true, Enhance = EnhanceCheck.IsChecked == true,
        Ocr = OcrCheck.IsChecked == true,
    };

    void UpdateAvailability()
    {
        var d = App.State.Current;
        Notice.IsOpen = true;
        if (d is null) Notice.Message = "Choose a printer first.";
        else if (!d.CanScan) Notice.Message = $"No scanner was found for {d.Name}. Connect it by network or USB, or install its scanner driver.";
        else Notice.IsOpen = false;
        UpdateButtons();
    }

    async Task LoadCapabilitiesAsync()
    {
        _caps = null;
        var session = App.State.Session;
        if (session?.Escl is null) return;
        _caps = await ScanService.GetCapabilitiesAsync(session);
        if (_caps is null) return;

        // only offer what the scanner can do
        var sources = new List<(string, ScanSource)>();
        if (_caps.Flatbed is not null) sources.Add(("Scanner glass (flatbed)", ScanSource.Flatbed));
        if (_caps.Feeder is not null) sources.Add(("Document feeder", ScanSource.Feeder));
        if (_caps.Feeder is not null && _caps.FeederDuplex) sources.Add(("Document feeder, both sides", ScanSource.FeederDuplex));
        if (sources.Count > 0)
        {
            var keep = Ui.Selected<ScanSource>(SourceCombo);
            _loading = true; Ui.Fill(SourceCombo, sources, keep); _loading = false;
        }
        var res = (_caps.Flatbed ?? _caps.Feeder)?.Resolutions;
        if (res is { Count: > 0 })
        {
            int keepDpi = Ui.Selected<int>(DpiCombo);
            _loading = true; Ui.Fill(DpiCombo, res.Select(r => ($"{r}", r)), res.OrderBy(r => Math.Abs(r - keepDpi)).First()); _loading = false;
        }
    }

    void UpdateButtons()
    {
        bool canScan = App.State.Current?.CanScan == true && _cts is null;
        bool has = Pages.Count > 0 && _cts is null;
        ScanButton.IsEnabled = canScan;
        ScanButtonText.Text = Pages.Count == 0 ? "Scan" : "Scan another page";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ScanButton, ScanButtonText.Text); // keep the spoken name equal to the visible label
        foreach (var b in new Button[] { SaveButton, SaveAsButton, ClearButton, TextButton }) b.IsEnabled = has;
        PrintButton.IsEnabled = has && App.State.Current?.CanPrint == true;
        EditBar.IsEnabled = has;
        ImportButton.IsEnabled = CameraButton.IsEnabled = _cts is null;
        EmptyState.Visibility = Pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Pages.Count == 0) Preview.Source = null;
    }

    // ============================================================ scanning

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State; var dev = s.Current; if (dev is null) return;
        var settings = ReadSettings();
        _cts = new CancellationTokenSource();
        ScanButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible; Progress.Visibility = Visibility.Visible; BusyRing.IsActive = Pages.Count == 0;
        StatusText.Text = settings.Source == ScanSource.Flatbed ? "Scanning…" : "Scanning from the feeder…";
        SavedBar.IsOpen = false;
        int count = 0;
        try
        {
            await foreach (var page in ScanService.ScanAsync(s.Session, dev, settings, _cts.Token))
            {
                var vm = new PageVm(page);
                Pages.Add(vm);
                await RefreshAsync(vm, updatePreview: true);
                Strip.SelectedItem = vm;
                count++;
                StatusText.Text = $"Scanned {count} page{(count == 1 ? "" : "s")}";
            }
            StatusText.Text = settings.Source == ScanSource.Flatbed
                ? "Done. Place the next page and press “Scan another page”, or save when you’re finished."
                : $"Done. {count} page{(count == 1 ? "" : "s")} scanned.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Scan cancelled."; }
        catch (Exception ex)
        {
            AppLog.Write("Scan: " + ex);
            StatusText.Text = "";
            await Ui.MessageAsync(XamlRoot, "Scan failed", FriendlyError(ex));
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            CancelButton.Visibility = Visibility.Collapsed; Progress.Visibility = Visibility.Collapsed; BusyRing.IsActive = false;
            UpdateButtons();
        }
    }

    static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException => "The scanner didn't answer. Check that the printer is on and connected, then try again.",
        EsclException or WiaException or InvalidOperationException => ex.Message,
        _ => ex.Message,
    };

    void Cancel_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); StatusText.Text = "Cancelling…"; }

    // ============================================================ page list / preview

    /// <summary>Re-render the thumbnail (and big preview when this is the selected page).</summary>
    async Task RefreshAsync(PageVm vm, bool updatePreview)
    {
        var page = vm.Page;
        var (preview, thumb) = await Task.Run(() =>
        {
            using var big = ImageTools.Render(page, 1600);
            using var small = ImageTools.Resize(big, 260);
            return (ImageTools.Encode(big, OutputFormat.Png), ImageTools.Encode(small, OutputFormat.Jpeg, 80));
        });
        vm.Update(await Ui.ToBitmapImageAsync(thumb), Pages.IndexOf(vm) + 1);
        if (updatePreview && ReferenceEquals(vm, Current) || updatePreview && Current is null)
        {
            int token = ++_previewToken;
            var img = await Ui.ToBitmapImageAsync(preview);
            if (token == _previewToken) { Preview.Source = img; EmptyState.Visibility = Visibility.Collapsed; }
        }
        else if (ReferenceEquals(vm, Current)) Preview.Source = await Ui.ToBitmapImageAsync(preview);
    }

    async void Strip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        for (int i = 0; i < Pages.Count; i++) if (Pages[i].Label.Length == 0) await RefreshAsync(Pages[i], false);
        for (int i = 0; i < Pages.Count; i++) Pages[i].Update(Pages[i].Thumb!, i + 1); // renumber after drag-reorder
        if (Current is { } vm) await RefreshAsync(vm, true);
        UpdateButtons();
    }

    // ============================================================ editing

    async Task EditAsync(Action<PageEdits> change)
    {
        if (Current is not { } vm) return;
        change(vm.Page.Edits);
        await RefreshAsync(vm, true);
    }

    async void RotateLeft_Click(object sender, RoutedEventArgs e) => await EditAsync(ed => ed.RotateLeft());
    async void RotateRight_Click(object sender, RoutedEventArgs e) => await EditAsync(ed => ed.RotateRight());

    async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string t } && Enum.TryParse<PageFilter>(t, out var f)) await EditAsync(ed => ed.Filter = f);
    }

    async void AutoCrop_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        var b = await Task.Run(() =>
        {
            var edits = vm.Page.Edits.Clone(); edits.Crop = null;
            using var bmp = ImageTools.Render(vm.Page.Original, edits, 1200);
            return ImageTools.DetectDocumentBounds(bmp);
        });
        if (b is null) { App.Window.Toast("Couldn't find the edges of the document. Use Crop to set them by hand.", InfoBarSeverity.Warning); return; }
        await EditAsync(ed => ed.Crop = b);
    }

    async void Straighten_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        var angle = await Task.Run(() =>
        {
            var edits = vm.Page.Edits.Clone(); edits.Straighten = 0; edits.Crop = null;
            using var bmp = ImageTools.Render(vm.Page.Original, edits, 1200);
            return ImageTools.DetectDocumentSkew(bmp);
        });
        if (Math.Abs(angle) < 0.2) { App.Window.Toast("This page already looks straight.", InfoBarSeverity.Informational); return; }
        await EditAsync(ed => { ed.Straighten = -angle; ed.Crop = null; });
        App.Window.Toast($"Straightened by {Math.Abs(angle):0.#}°. Use Auto-crop to trim the edges.", InfoBarSeverity.Success);
    }

    async void Adjust_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Current is not { } vm) return;
        int token = ++_adjustToken;
        await Task.Delay(150);
        if (token != _adjustToken) return;
        vm.Page.Edits.Brightness = (int)BrightnessSlider.Value; vm.Page.Edits.Contrast = (int)ContrastSlider.Value;
        await RefreshAsync(vm, true);
    }

    void AdjustReset_Click(object sender, RoutedEventArgs e) { BrightnessSlider.Value = 0; ContrastSlider.Value = 0; }

    async void ResetEdits_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        vm.Page.Edits = new PageEdits();
        BrightnessSlider.Value = 0; ContrastSlider.Value = 0;
        await RefreshAsync(vm, true);
    }

    async void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        int idx = Pages.IndexOf(vm);
        Pages.Remove(vm);
        if (Pages.Count > 0) Strip.SelectedIndex = Math.Min(idx, Pages.Count - 1);
        else { Preview.Source = null; }
        for (int i = 0; i < Pages.Count; i++) Pages[i].Update(Pages[i].Thumb!, i + 1);
        UpdateButtons();
        await Task.CompletedTask;
    }

    async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, "Clear all pages?", "The scanned pages haven't been saved to a file unless you chose Save. Remove them from the list?", "Clear all")) return;
        Pages.Clear(); Preview.Source = null; SavedBar.IsOpen = false; StatusText.Text = "";
        UpdateButtons();
    }

    // ---------------------------------------------------------- crop dialog

    async void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        var probe = vm.Page.Edits.Clone();
        probe.Crop = null; probe.Filter = PageFilter.Original; probe.Brightness = probe.Contrast = 0; probe.Texts.Clear(); probe.Images.Clear();
        var (bytes, w, h) = await Task.Run(() => { using var b = ImageTools.Render(vm.Page.Original, probe, 900); return (ImageTools.Encode(b, OutputFormat.Jpeg, 85), b.Width, b.Height); });

        double scale = Math.Min(560.0 / w, 400.0 / h), dw = w * scale, dh = h * scale;
        var image = new Image { Width = dw, Height = dh, Stretch = Stretch.Fill, Source = await Ui.ToBitmapImageAsync(bytes) };
        var frame = new Rectangle { Stroke = new SolidColorBrush(Colors.Gold), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 }, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 215, 0)) };
        var canvas = new Canvas { Width = dw, Height = dh };
        canvas.Children.Add(frame);
        var stack = new Grid { Width = dw, Height = dh, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(image); stack.Children.Add(canvas);

        Slider Mk(string header, double v) => new() { Header = header, Minimum = 0, Maximum = 90, Value = v, StepFrequency = 0.5 };
        var c = vm.Page.Edits.Crop;
        var sl = Mk("Left edge %", c is null ? 0 : c.Value.X * 100);
        var st = Mk("Top edge %", c is null ? 0 : c.Value.Y * 100);
        var sr = Mk("Right edge %", c is null ? 0 : (1 - c.Value.X - c.Value.W) * 100);
        var sb = Mk("Bottom edge %", c is null ? 0 : (1 - c.Value.Y - c.Value.H) * 100);

        void Layout()
        {
            double l = sl.Value / 100, t = st.Value / 100, r = sr.Value / 100, b = sb.Value / 100;
            double rw = Math.Max(0.05, 1 - l - r), rh = Math.Max(0.05, 1 - t - b);
            Canvas.SetLeft(frame, l * dw); Canvas.SetTop(frame, t * dh); frame.Width = rw * dw; frame.Height = rh * dh;
        }
        foreach (var s in new[] { sl, st, sr, sb }) s.ValueChanged += (_, _) => Layout();
        Layout();

        var detect = new Button { Content = "Detect document" };
        detect.Click += async (_, _) =>
        {
            var det = await Task.Run(() => { using var bmp = ImageTools.Load(bytes); return ImageTools.DetectDocumentBounds(bmp); });
            if (det is null) return;
            sl.Value = det.Value.X * 100; st.Value = det.Value.Y * 100;
            sr.Value = (1 - det.Value.X - det.Value.W) * 100; sb.Value = (1 - det.Value.Y - det.Value.H) * 100;
        };
        var none = new Button { Content = "Whole page" };
        none.Click += (_, _) => { sl.Value = st.Value = sr.Value = sb.Value = 0; };

        var sliders = new StackPanel { Spacing = 4, Width = 240 };
        sliders.Children.Add(sl); sliders.Children.Add(st); sliders.Children.Add(sr); sliders.Children.Add(sb);
        sliders.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { detect, none } });
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        content.Children.Add(stack); content.Children.Add(sliders);

        var dlg = new ContentDialog { Title = "Crop", Content = content, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        float x = (float)(sl.Value / 100), y = (float)(st.Value / 100);
        float cw = (float)Math.Max(0.05, 1 - sl.Value / 100 - sr.Value / 100), ch = (float)Math.Max(0.05, 1 - st.Value / 100 - sb.Value / 100);
        await EditAsync(ed => ed.Crop = x == 0 && y == 0 && cw >= 0.999f && ch >= 0.999f ? null : (x, y, cw, ch));
    }

    // ---------------------------------------------------------- text and signature

    static readonly (string Name, string Hex)[] InkColors = { ("Black", "#000000"), ("Blue", "#0B3FBF"), ("Red", "#C42B1C"), ("Green", "#107C10"), ("White", "#FFFFFF") };

    StackPanel PlacementSliders(out Slider x, out Slider y)
    {
        x = new Slider { Header = "Position: from left %", Minimum = 0, Maximum = 95, Value = 10 };
        y = new Slider { Header = "Position: from top %", Minimum = 0, Maximum = 95, Value = 10 };
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(x); p.Children.Add(y);
        return p;
    }

    async void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;
        var text = new TextBox { Header = "Text", AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap };
        var size = new Slider { Header = "Size (% of page height)", Minimum = 1, Maximum = 12, Value = 3, StepFrequency = 0.5 };
        var color = new ComboBox { Header = "Colour", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(color, InkColors.Select(c => (c.Name, c.Hex)), "#000000");
        var bold = new CheckBox { Content = "Bold" };
        var place = PlacementSliders(out var px, out var py);
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(text); panel.Children.Add(size); panel.Children.Add(color); panel.Children.Add(bold); panel.Children.Add(place);

        var dlg = new ContentDialog { Title = "Add text", Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(text.Text)) return;
        var annotation = new TextAnnotation(text.Text, (float)(px.Value / 100), (float)(py.Value / 100), (float)(size.Value / 100), Ui.Selected<string>(color) ?? "#000000", bold.IsChecked == true);
        await EditAsync(ed => ed.Texts.Add(annotation));
    }

    async void Sign_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;
        var strokes = new List<List<Windows.Foundation.Point>>();
        List<Windows.Foundation.Point>? active = null;
        Polyline? live = null;
        var pad = new Canvas { Width = 420, Height = 160, Background = new SolidColorBrush(Colors.White) };
        var border = new Border { BorderBrush = new SolidColorBrush(Colors.Gray), BorderThickness = new Thickness(1), Child = pad, Width = 422, Height = 162 };

        pad.PointerPressed += (_, a) =>
        {
            pad.CapturePointer(a.Pointer);
            var p = a.GetCurrentPoint(pad).Position;
            active = new List<Windows.Foundation.Point> { p }; strokes.Add(active);
            live = new Polyline { Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            live.Points.Add(p); pad.Children.Add(live);
        };
        pad.PointerMoved += (_, a) =>
        {
            if (active is null || live is null) return;
            var p = a.GetCurrentPoint(pad).Position; active.Add(p); live.Points.Add(p);
        };
        pad.PointerReleased += (_, a) => { active = null; live = null; pad.ReleasePointerCapture(a.Pointer); };

        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => { strokes.Clear(); pad.Children.Clear(); };
        var width = new Slider { Header = "Signature width (% of page width)", Minimum = 10, Maximum = 60, Value = 28 };
        var place = PlacementSliders(out var px, out var py);
        px.Value = 55; py.Value = 85;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Sign in the box using your mouse, pen or finger.", Opacity = 0.7 });
        panel.Children.Add(border); panel.Children.Add(clear); panel.Children.Add(width); panel.Children.Add(place);

        var dlg = new ContentDialog { Title = "Add signature", Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, PrimaryButtonText = "Place on page", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || strokes.Count == 0) return;

        var png = RenderSignature(strokes, 420, 160);
        var annotation = new ImageAnnotation(png, (float)(px.Value / 100), (float)(py.Value / 100), (float)(width.Value / 100));
        await EditAsync(ed => ed.Images.Add(annotation));
    }

    /// <summary>Strokes → transparent PNG, trimmed to the ink.</summary>
    static byte[] RenderSignature(List<List<Windows.Foundation.Point>> strokes, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w * 2, h * 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 20, 20, 90), 5) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            foreach (var s in strokes)
            {
                var pts = s.Select(p => new System.Drawing.PointF((float)p.X * 2, (float)p.Y * 2)).ToArray();
                if (pts.Length == 1) g.FillEllipse(pen.Brush, pts[0].X - 2.5f, pts[0].Y - 2.5f, 5, 5);
                else if (pts.Length > 1) g.DrawLines(pen, pts);
            }
        }
        // trim transparent margin
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) if (bmp.GetPixel(x, y).A > 0) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX <= minX) maxX = minX + 1;
        if (maxY <= minY) maxY = minY + 1;
        using var trimmed = bmp.Clone(new System.Drawing.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1), bmp.PixelFormat);
        using var ms = new MemoryStream();
        trimmed.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    // ============================================================ output

    List<ScannedPage> AllPages() => Pages.Select(p => p.Page).ToList();

    async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(null, null);

    async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var format = Ui.Selected<OutputFormat>(FormatCombo);
        string? folder, stem;
        if (format is OutputFormat.Pdf or OutputFormat.Tiff || Pages.Count == 1)
        {
            var ext = ImageTools.Extension(format);
            var path = await Ui.PickSaveFileAsync(ExportService.SuggestName(), format.ToString().ToUpperInvariant() + " file", ext);
            if (path is null) return;
            folder = Path.GetDirectoryName(path); stem = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            folder = await Ui.PickFolderAsync();
            if (folder is null) return;
            stem = ExportService.SuggestName();
        }
        await SaveAsync(folder, stem);
    }

    async Task SaveAsync(string? folder, string? name)
    {
        if (Pages.Count == 0) return;
        var s = App.State;
        var format = Ui.Selected<OutputFormat>(FormatCombo);
        bool searchable = format == OutputFormat.Pdf && OcrCheck.IsChecked == true;
        if (searchable && !OcrService.IsAvailable)
        {
            searchable = false;
            App.Window.Toast("Text recognition isn't available (no Windows OCR language installed), saving without a text layer.", InfoBarSeverity.Warning);
        }

        SaveButton.IsEnabled = SaveAsButton.IsEnabled = false; Progress.Visibility = Visibility.Visible;
        StatusText.Text = searchable ? "Recognising text and saving…" : "Saving…";
        try
        {
            var files = await ExportService.SaveAsync(AllPages(), format, folder ?? s.Settings.EffectiveSaveFolder, name ?? ExportService.SuggestName(), searchable);
            _lastSaved = files[0];
            SavedBar.Message = files.Count == 1 ? $"Saved {Path.GetFileName(files[0])} to {Path.GetDirectoryName(files[0])}" : $"Saved {files.Count} files to {Path.GetDirectoryName(files[0])}";
            SavedBar.IsOpen = true;
            StatusText.Text = "";
            if (s.Settings.OpenFolderAfterSave) Ui.ShowInFolder(files[0]);
        }
        catch (Exception ex)
        {
            AppLog.Write("Save: " + ex);
            StatusText.Text = "";
            await Ui.MessageAsync(XamlRoot, "Couldn't save", ex.Message);
        }
        finally { Progress.Visibility = Visibility.Collapsed; UpdateButtons(); }
    }

    void OpenSaved_Click(object sender, RoutedEventArgs e) { if (_lastSaved is not null) Ui.OpenFile(_lastSaved); }
    void ShowSaved_Click(object sender, RoutedEventArgs e) { if (_lastSaved is not null) Ui.ShowInFolder(_lastSaved); }

    async void Print_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State; var dev = s.Current;
        if (dev is null || Pages.Count == 0) return;

        var copies = new NumberBox { Header = "Copies", Value = 1, Minimum = 1, Maximum = 99, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var color = new ComboBox { Header = "Colour", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(color, new[] { ("Colour", true), ("Black and white", false) }, Ui.Selected<ScanColor>(ColorCombo) == ScanColor.Color);
        var paper = new ComboBox { Header = "Paper size", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(paper, PaperChoice.All.Select(p => (p.Name, p)), PaperChoice.Default);
        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(copies); panel.Children.Add(color); panel.Children.Add(paper);

        var dlg = new ContentDialog { Title = $"Print {Pages.Count} page{(Pages.Count == 1 ? "" : "s")} on {dev.Name}", Content = panel, PrimaryButtonText = "Print", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        Progress.Visibility = Visibility.Visible; StatusText.Text = "Sending to the printer…";
        try
        {
            var pages = AllPages();
            var src = await Task.Run(() =>
            {
                var bmps = ExportService.RenderAll(pages);
                try { return PrintSource.FromBitmaps(bmps, pages[0].Dpi, "Scan"); } finally { bmps.ForEach(b => b.Dispose()); }
            });
            await PrintService.PrintAsync(dev, s.Session, src, new PrintOptions
            {
                Copies = (int)Math.Max(1, copies.Value), Color = Ui.Selected<bool>(color), Paper = Ui.Selected<PaperChoice>(paper) ?? PaperChoice.Default,
                Route = s.Settings.PrintRoute,
            });
            App.Window.Toast("Sent to the printer.", InfoBarSeverity.Success);
            StatusText.Text = "";
        }
        catch (Exception ex) { AppLog.Write("Print scan: " + ex); StatusText.Text = ""; await Ui.MessageAsync(XamlRoot, "Couldn't print", ex.Message); }
        finally { Progress.Visibility = Visibility.Collapsed; }
    }

    async void Text_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        if (!OcrService.IsAvailable)
        {
            await Ui.MessageAsync(XamlRoot, "Text recognition isn't available", "Windows needs an OCR language pack. Open Settings > Time & language > Language & region, add your language, and make sure “Optical character recognition” is installed for it.");
            return;
        }
        Progress.Visibility = Visibility.Visible; StatusText.Text = "Recognising text…";
        OcrResult? result = null;
        int turned = 0;
        try { (result, turned) = await ExportService.ReadPageTextAsync(vm.Page); }
        catch (Exception ex) { AppLog.Write("OCR: " + ex); await Ui.MessageAsync(XamlRoot, "Text recognition failed", ex.Message); }
        finally { Progress.Visibility = Visibility.Collapsed; StatusText.Text = ""; }
        if (result is null) return;

        var box = new TextBox { Text = string.IsNullOrWhiteSpace(result.Text) ? "(No text was found on this page.)" : result.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 520, Height = 320, IsReadOnly = false };
        var dlg = new ContentDialog
        {
            Title = $"Text on page {Pages.IndexOf(vm) + 1} ({result.Language}{(turned == 0 ? "" : $", page read turned {turned}°")})", Content = box,
            PrimaryButtonText = "Copy", SecondaryButtonText = "Save as .txt", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.Primary)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(box.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            App.Window.Toast("Text copied.", InfoBarSeverity.Success);
        }
        else if (r == ContentDialogResult.Secondary)
        {
            var path = await Ui.PickSaveFileAsync(ExportService.SuggestName("Scan text"), "Text file", ".txt");
            if (path is not null) { await File.WriteAllTextAsync(path, box.Text); App.Window.Toast("Saved " + Path.GetFileName(path), InfoBarSeverity.Success); }
        }
    }

    void ShareSaved_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSaved is null) return;
        try { Ui.Share(_lastSaved); }
        catch (Exception ex) { AppLog.Write("Share: " + ex); App.Window.Toast("Couldn't open the Windows share sheet: " + ex.Message, InfoBarSeverity.Warning); }
    }

    async void Camera_Click(object sender, RoutedEventArgs e)
    {
        Services.CameraCapture.Result? shot;
        try { shot = await Services.CameraCapture.CaptureAsync(XamlRoot); }
        catch (Exception ex) { AppLog.Write("Camera: " + ex); await Ui.MessageAsync(XamlRoot, "Camera error", ex.Message); return; }
        if (shot is null) return;

        var page = new ScannedPage(shot.Jpeg, 150) { SourceName = "Camera" };
        if (shot.CleanUp)
        {
            await Task.Run(() =>
            {
                using var bmp = ImageTools.Load(shot.Jpeg);
                var skew = ImageTools.DetectDocumentSkew(bmp);
                if (Math.Abs(skew) >= 0.3) page.Edits.Straighten = -skew;
                using var rotated = page.Edits.Straighten == 0 ? (System.Drawing.Bitmap)bmp.Clone() : ImageTools.RotateArbitrary(bmp, page.Edits.Straighten);
                page.Edits.Crop = ImageTools.DetectDocumentBounds(rotated);
                page.Edits.Filter = PageFilter.Enhance;
            });
        }
        var vm = new PageVm(page);
        Pages.Add(vm);
        await RefreshAsync(vm, true);
        Strip.SelectedItem = vm;
        UpdateButtons();
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        await ImportFilesAsync(await Ui.PickFilesAsync(".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"));
    }

    async Task ImportFilesAsync(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(f);
                int dpi = 300;
                using (var probe = ImageTools.Load(bytes)) if (probe.HorizontalResolution is > 50 and < 1300) dpi = (int)Math.Round(probe.HorizontalResolution);
                var page = new ScannedPage(bytes, dpi) { SourceName = Path.GetFileName(f) };
                var vm = new PageVm(page);
                Pages.Add(vm);
                await RefreshAsync(vm, true);
                Strip.SelectedItem = vm;
            }
            catch (Exception ex) { App.Window.Toast($"Couldn't open {Path.GetFileName(f)}: {ex.Message}", InfoBarSeverity.Warning); }
        }
        UpdateButtons();
    }
}
