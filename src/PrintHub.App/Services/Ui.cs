using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PrintHub.Core.Imaging;
using PrintHub.Core.Ipp;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace PrintHub.App.Services;

/// <summary>Small UI helpers shared by the pages: images, pickers, dialogs and reusable visuals.</summary>
public static class Ui
{
    public static async Task<BitmapImage> ToBitmapImageAsync(byte[] bytes, int decodeWidth = 0)
    {
        var img = new BitmapImage();
        if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
        using var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(bytes.AsBuffer());
        ms.Seek(0);
        await img.SetSourceAsync(ms);
        return img;
    }

    public static Task<BitmapImage> ToBitmapImageAsync(System.Drawing.Bitmap bmp) =>
        ToBitmapImageAsync(ImageTools.Encode(bmp, OutputFormat.Png));

    // ---------------------------------------------------------------- pickers

    static T Init<T>(T picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        return picker;
    }

    public static async Task<List<string>> PickFilesAsync(params string[] extensions)
    {
        var p = Init(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        foreach (var e in extensions) p.FileTypeFilter.Add(e);
        if (extensions.Length == 0) p.FileTypeFilter.Add("*");
        var files = await p.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? new();
    }

    public static async Task<string?> PickFolderAsync()
    {
        var p = Init(new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        p.FileTypeFilter.Add("*");
        return (await p.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(string suggestedName, string description, string extension)
    {
        var p = Init(new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName });
        p.FileTypeChoices.Add(description, new List<string> { extension });
        return (await p.PickSaveFileAsync())?.Path;
    }

    // ---------------------------------------------------------------- dialogs

    public static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        var d = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "OK", XamlRoot = root };
        await d.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary = "Yes", string close = "Cancel")
    {
        var d = new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary, XamlRoot = root,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> PromptAsync(XamlRoot root, string title, string message, string placeholder = "", string primary = "OK")
    {
        var box = new TextBox { PlaceholderText = placeholder, MinWidth = 320 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var d = new ContentDialog { Title = title, Content = panel, PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = root };
        return await d.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    // ---------------------------------------------------------------- share

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, in Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    /// <summary>Open the Windows share sheet for a file (mail, Nearby Share, Teams…).</summary>
    public static void Share(string path)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        var interop = Windows.ApplicationModel.DataTransfer.DataTransferManager.As<IDataTransferManagerInterop>();
        var ptr = interop.GetForWindow(hwnd, DataTransferManagerIid);
        var dtm = WinRT.MarshalInterface<Windows.ApplicationModel.DataTransfer.DataTransferManager>.FromAbi(ptr);
        dtm.DataRequested += async (_, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                args.Request.Data.Properties.Title = file.Name;
                args.Request.Data.SetStorageItems(new[] { file });
            }
            catch (Exception ex) { args.Request.FailWithDisplayText("Couldn't share the file: " + ex.Message); }
            finally { deferral.Complete(); }
        };
        interop.ShowShareUIForWindow(hwnd);
    }

    // ---------------------------------------------------------------- combo helpers

    /// <summary>Fill a ComboBox with (text, value) pairs and select <paramref name="selected"/>.</summary>
    public static void Fill<T>(ComboBox cb, IEnumerable<(string Text, T Value)> items, T? selected = default)
    {
        cb.Items.Clear();
        foreach (var (text, value) in items) cb.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        Select(cb, selected);
    }

    public static void Select<T>(ComboBox cb, T? value)
    {
        var item = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? cb.Items.OfType<ComboBoxItem>().FirstOrDefault();
        cb.SelectedItem = item;
    }

    public static T? Selected<T>(ComboBox cb) => cb.SelectedItem is ComboBoxItem { Tag: T v } ? v : default;

    public static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    public static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ---------------------------------------------------------------- visuals

    public static Windows.UI.Color ParseColor(string hex, Windows.UI.Color fallback)
    {
        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6) return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
        }
        catch { }
        return fallback;
    }

    /// <summary>One ink/toner row: coloured bar with name and percentage.</summary>
    public static FrameworkElement SupplyRow(SupplyLevel s)
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        grid.Children.Add(new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var color = ParseColor(s.ColorHex.Split(',')[0], Colors.Gray);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Max(0, s.Percent), Height = 10, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        if (s.Percent < 0) bar.IsIndeterminate = false;
        Grid.SetColumn(bar, 1); grid.Children.Add(bar);

        var pct = new TextBlock
        {
            Text = s.Percent < 0 ? "—" : s.IsLow ? $"{s.Percent}% ⚠" : $"{s.Percent}%",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        if (s.IsLow) pct.Foreground = new SolidColorBrush(Colors.OrangeRed);
        Grid.SetColumn(pct, 2); grid.Children.Add(pct);
        return grid;
    }

    public static FontIcon Glyph(string glyph, double size = 20) => new() { Glyph = glyph, FontSize = size };

    /// <summary>A pastel brush from the palette in App.xaml: hue = Pink, Mint, Lavender, Peach, Sky or Butter (Ink = icon colour).</summary>
    public static Brush Pastel(string hue, string kind = "Ink") => (Brush)Application.Current.Resources[hue + kind];

    public static Button Tile(string glyph, string title, string subtitle, RoutedEventHandler click, bool enabled = true, string hue = "Lavender")
    {
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Glyph(glyph, 30);
        icon.Foreground = Pastel(hue);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var b = new Button { Content = stack, Style = (Style)Application.Current.Resources["TileButton"], IsEnabled = enabled };
        b.Margin = new Thickness(3);           // room for the border at the GridView edges
        b.Click += click;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title);
        return b;
    }
}
