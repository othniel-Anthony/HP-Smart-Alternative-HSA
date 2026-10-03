using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Escl;
using PrintHub.Core.Imaging;
using PrintHub.Core.Scanning;
using PrintHub.Core.Settings;

namespace PrintHub.App.Pages;

public sealed partial class ShortcutsPage : Page
{
    string? _lastResult;
    bool _running;

    public ShortcutsPage() => InitializeComponent();

    void Page_Loaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged += OnChanged; Build(); }
    void Page_Unloaded(object sender, RoutedEventArgs e) { App.State.CurrentChanged -= OnChanged; }
    void OnChanged() => DispatcherQueue.TryEnqueue(Build);

    static string Summary(ShortcutDef s)
    {
        if (s.Kind == ShortcutKind.Copy) return s.IdCard ? "Copy both sides of an ID card" : $"Copy {s.CopyCount}× {(s.Color == ScanColor.Color ? "colour" : "black & white")}";
        var parts = new List<string> { s.Color switch { ScanColor.Color => "Colour", ScanColor.Grayscale => "Gray", _ => "B&W" }, $"{s.Dpi} dpi", s.Format.ToString().ToUpperInvariant() };
        if (s.Searchable && s.Format == OutputFormat.Pdf) parts.Add("searchable");
        if (s.PrintCopies > 0) parts.Add($"print {s.PrintCopies}");
        return string.Join(" · ", parts);
    }

    void Build()
    {
        var dev = App.State.Current;
        Notice.IsOpen = dev is null || !dev.CanScan;
        Notice.Message = dev is null ? "Choose a printer to run shortcuts." : $"{dev.Name} has no reachable scanner, so shortcuts can't run.";
        Cards.Items.Clear();
        foreach (var s in App.State.Settings.Shortcuts) Cards.Items.Add(Card(s, dev is { CanScan: true } && !_running));
    }

    FrameworkElement Card(ShortcutDef s, bool enabled)
    {
        var border = new Border { Style = (Style)Application.Current.Resources["CardStyle"], Width = 250, Margin = new Thickness(4) };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new FontIcon { Glyph = s.Glyph, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Left });
        stack.Children.Add(new TextBlock { Text = s.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = Summary(s), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var run = new Button { Content = "Run", Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = enabled };
        run.Click += async (_, _) => await RunAsync(s);
        var edit = new Button { Content = "Edit" }; edit.Click += async (_, _) => await EditAsync(s, false);
        var del = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetName(del, "Delete shortcut " + s.Name);
        del.Click += async (_, _) =>
        {
            if (!await Ui.ConfirmAsync(XamlRoot, "Delete shortcut?", $"Delete “{s.Name}”?", "Delete")) return;
            App.State.Settings.Shortcuts.Remove(s); App.State.SaveSettings(); Build();
        };
        row.Children.Add(run); row.Children.Add(edit); row.Children.Add(del);
        stack.Children.Add(row);
        border.Child = stack;
        return border;
    }

    async Task RunAsync(ShortcutDef s)
    {
        var st = App.State; var dev = st.Current; if (dev is null || _running) return;
        _running = true; Build(); Progress.Visibility = Visibility.Visible; ResultBar.IsOpen = false;
        var progress = new Progress<string>(t => StatusText.Text = t);
        try
        {
            var files = await ShortcutRunner.RunAsync(s, dev, st.Session, st.Settings,
                promptNextSide: async () => await Ui.MessageAsync(XamlRoot, "Flip the card", "Turn the card over, place the back on the scanner glass, then press OK."),
                progress);
            if (files.Count > 0)
            {
                _lastResult = files[0];
                ResultBar.Message = $"{s.Name}: saved {Path.GetFileName(files[0])}";
                ResultBar.IsOpen = true;
                if (s.OpenAfter) Ui.OpenFile(files[0]);
            }
            else App.Window.Toast($"{s.Name} finished.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Shortcut {s.Name}: {ex}");
            StatusText.Text = "";
            await Ui.MessageAsync(XamlRoot, $"“{s.Name}” failed", ex is HttpRequestException ? "The scanner didn't answer. Check that the printer is on and connected." : ex.Message);
        }
        finally { _running = false; Progress.Visibility = Visibility.Collapsed; Build(); }
    }

    void OpenResult_Click(object sender, RoutedEventArgs e) { if (_lastResult is not null) Ui.OpenFile(_lastResult); }
    void ShowResult_Click(object sender, RoutedEventArgs e) { if (_lastResult is not null) Ui.ShowInFolder(_lastResult); }

    async void New_Click(object sender, RoutedEventArgs e) => await EditAsync(new ShortcutDef(), true);

    async Task EditAsync(ShortcutDef s, bool isNew)
    {
        var name = new TextBox { Header = "Name", Text = s.Name };
        var kind = new ComboBox { Header = "What it does", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(kind, new[] { ("Scan and save", ShortcutKind.Scan), ("Copy", ShortcutKind.Copy) }, s.Kind);
        var source = new ComboBox { Header = "Scan from", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(source, new[] { ("Scanner glass", ScanSource.Flatbed), ("Document feeder", ScanSource.Feeder), ("Feeder, both sides", ScanSource.FeederDuplex) }, s.Source);
        var color = new ComboBox { Header = "Colour", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(color, new[] { ("Colour", ScanColor.Color), ("Grayscale", ScanColor.Grayscale), ("Black and white", ScanColor.BlackAndWhite) }, s.Color);
        var dpi = new ComboBox { Header = "Resolution (dpi)", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(dpi, new[] { 75, 150, 200, 300, 600, 1200 }.Select(d => ($"{d}", d)), s.Dpi);
        var paper = new ComboBox { Header = "Original size", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(paper, PaperSize.All.Select(p => (p.Name, p.Name)), s.PaperName);
        var format = new ComboBox { Header = "Save as", HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Fill(format, new[] { ("PDF", OutputFormat.Pdf), ("JPEG", OutputFormat.Jpeg), ("PNG", OutputFormat.Png), ("TIFF", OutputFormat.Tiff) }, s.Format);
        var crop = new CheckBox { Content = "Crop to the document", IsChecked = s.AutoCrop };
        var enhance = new CheckBox { Content = "Sharpen text, whiten paper", IsChecked = s.Enhance };
        var ocr = new CheckBox { Content = "Make PDF searchable", IsChecked = s.Searchable };
        var open = new CheckBox { Content = "Open the file when done", IsChecked = s.OpenAfter };
        var folder = new TextBox { Header = "Save in folder (empty = default)", Text = s.Folder ?? "", PlaceholderText = App.State.Settings.EffectiveSaveFolder };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => { var f = await Ui.PickFolderAsync(); if (f is not null) folder.Text = f; };
        var printCopies = new NumberBox { Header = "Also print copies (0 = no)", Value = s.PrintCopies, Minimum = 0, Maximum = 99, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var copies = new NumberBox { Header = "Copies (for copy shortcuts)", Value = s.CopyCount, Minimum = 1, Maximum = 99, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var id = new CheckBox { Content = "ID card copy (both sides on one page)", IsChecked = s.IdCard };

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        foreach (UIElement el in new UIElement[] { name, kind, source, color, dpi, paper, format, crop, enhance, ocr, open, folder, browse, printCopies, copies, id }) panel.Children.Add(el);

        var dlg = new ContentDialog
        {
            Title = isNew ? "New shortcut" : "Edit shortcut", Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        s.Name = string.IsNullOrWhiteSpace(name.Text) ? "Shortcut" : name.Text.Trim();
        s.Kind = Ui.Selected<ShortcutKind>(kind);
        s.Glyph = s.Kind == ShortcutKind.Copy ? "" : s.Glyph == "" ? "" : s.Glyph;
        s.Source = Ui.Selected<ScanSource>(source); s.Color = Ui.Selected<ScanColor>(color);
        s.Dpi = Ui.Selected<int>(dpi) is var d && d > 0 ? d : 300;
        s.PaperName = Ui.Selected<string>(paper) ?? PaperSize.Auto.Name; s.Format = Ui.Selected<OutputFormat>(format);
        s.AutoCrop = crop.IsChecked == true; s.Enhance = enhance.IsChecked == true; s.Searchable = ocr.IsChecked == true; s.OpenAfter = open.IsChecked == true;
        s.Folder = string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim();
        s.PrintCopies = (int)Math.Max(0, printCopies.Value); s.CopyCount = (int)Math.Max(1, copies.Value); s.IdCard = id.IsChecked == true;
        if (isNew) App.State.Settings.Shortcuts.Add(s);
        App.State.SaveSettings();
        Build();
    }
}
