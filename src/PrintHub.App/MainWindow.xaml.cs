using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PrintHub.App.Pages;
using PrintHub.App.Services;
using PrintHub.Core.Discovery;

namespace PrintHub.App;

public sealed partial class MainWindow : Window
{
    readonly Dictionary<string, Type> _pages = new()
    {
        ["home"] = typeof(HomePage), ["print"] = typeof(PrintPage), ["scan"] = typeof(ScanPage), ["copy"] = typeof(CopyPage),
        ["shortcuts"] = typeof(ShortcutsPage), ["printer"] = typeof(PrinterPage), ["web"] = typeof(WebPage), ["maintenance"] = typeof(MaintenancePage), ["hpmaintenance"] = typeof(HpMaintenancePage), ["canonmaintenance"] = typeof(CanonMaintenancePage), ["settings"] = typeof(SettingsPage),
    };
    bool _updatingCombo;
    DispatcherQueueTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();
        Title = "HP Smart Alternative (HSA)";
        TitleText.Text = $"{Title}  ·  v{AppState.Version}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch { /* older Windows: plain background */ }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HSA.ico")); } catch { }

        App.State.DevicesChanged += () => DispatcherQueue.TryEnqueue(RebuildCombo);
        App.State.CurrentChanged += () => DispatcherQueue.TryEnqueue(UpdateHeader);
        App.State.StatusChanged += () => DispatcherQueue.TryEnqueue(UpdateHeader);

        Root.Loaded += (_, _) => StartupTrace.Mark("Window content loaded (first frame)");
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && _pages.TryGetValue(tag, out var page))
            ContentFrame.Navigate(page, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }

    public void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    public XamlRoot XamlRootForDialogs => Root.XamlRoot;

    /// <summary>Non-blocking message at the bottom of the window.</summary>
    public void Toast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, int seconds = 6)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToastBar.Message = message; ToastBar.Severity = severity; ToastBar.IsOpen = true;
            _toastTimer?.Stop();
            _toastTimer = DispatcherQueue.CreateTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? Math.Max(seconds, 10) : seconds);
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += (_, _) => ToastBar.IsOpen = false;
            _toastTimer.Start();
        });
    }

    void RebuildCombo()
    {
        _updatingCombo = true;
        try
        {
            PrinterCombo.Items.Clear();
            foreach (var d in App.State.Devices) PrinterCombo.Items.Add(new ComboBoxItem { Content = $"{d.Name}  ·  {(d.Connection.Length > 0 ? d.Connection : "saved")}", Tag = d });
            var cur = App.State.Current;
            if (cur is not null) PrinterCombo.SelectedItem = PrinterCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, cur));
            BusyRing.IsActive = App.State.Discovering || App.State.Connecting;
            RefreshButton.IsEnabled = !App.State.Discovering;
            if (App.State.Devices.Count == 0 && !App.State.Discovering) StateText.Text = "No printers found. Check power and network, or add one by address.";
        }
        finally { _updatingCombo = false; }
        UpdateHeader();
    }

    void UpdateHeader()
    {
        var s = App.State;
        // Epson-only tools: the entry exists only while an Epson printer is selected
        bool epson = s.Current?.IsEpson == true;
        MaintenanceItem.Visibility = epson ? Visibility.Visible : Visibility.Collapsed;
        if (!epson && ReferenceEquals(Nav.SelectedItem, MaintenanceItem)) NavigateTo("home");
        bool hp = s.Current?.IsHp == true;
        HpMaintenanceItem.Visibility = hp ? Visibility.Visible : Visibility.Collapsed;
        if (!hp && ReferenceEquals(Nav.SelectedItem, HpMaintenanceItem)) NavigateTo("home");
        bool canon = s.Current?.IsCanon == true;
        CanonMaintenanceItem.Visibility = canon ? Visibility.Visible : Visibility.Collapsed;
        if (!canon && ReferenceEquals(Nav.SelectedItem, CanonMaintenanceItem)) NavigateTo("home");

        BusyRing.IsActive = s.Discovering || s.Connecting;
        if (s.Current is null) { StateDot.Fill = new SolidColorBrush(Colors.Gray); if (s.Devices.Count > 0 || s.Discovering) StateText.Text = s.Discovering ? "Searching for printers…" : "Choose a printer"; return; }

        StateText.Text = s.StateText + (s.Session?.ViaUsb == true ? "  ·  via USB" : "");
        StateDot.Fill = new SolidColorBrush(s.Connecting ? Colors.Gold : s.IsReady ? (s.Status?.Alerts.Any() == true ? Colors.Orange : Colors.LimeGreen) : Colors.OrangeRed);
        _updatingCombo = true;
        try { PrinterCombo.SelectedItem = PrinterCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, s.Current)); }
        finally { _updatingCombo = false; }
    }

    async void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCombo) return;
        if (PrinterCombo.SelectedItem is ComboBoxItem { Tag: PrinterDevice d } && !ReferenceEquals(d, App.State.Current))
            await App.State.SelectAsync(d);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await App.State.DiscoverAsync();

    async void AddPrinter_Click(object sender, RoutedEventArgs e)
    {
        var host = await Ui.PromptAsync(Root.XamlRoot, "Add a printer by address",
            "Enter the printer's IP address or host name (shown on its control panel or network configuration page).", "e.g. 192.168.1.50", "Add");
        if (string.IsNullOrWhiteSpace(host)) return;
        BusyRing.IsActive = true;
        try
        {
            var dev = await App.State.AddByAddressAsync(host);
            if (dev is null) Toast($"Nothing answered at {host}. Make sure the printer is on and on the same network.", InfoBarSeverity.Warning);
            else Toast($"Added {dev.Name}.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Toast("Could not add the printer: " + ex.Message, InfoBarSeverity.Error); }
        finally { BusyRing.IsActive = false; }
    }
}
