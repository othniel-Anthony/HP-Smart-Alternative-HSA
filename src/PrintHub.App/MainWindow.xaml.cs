using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PrintHub.App.Pages;
using PrintHub.App.Services;
using PrintHub.Core.Discovery;
using PrintHub.Core.Updates;
using Microsoft.UI.Xaml.Automation;

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

        App.Updates.Changed += () => DispatcherQueue.TryEnqueue(RefreshUpdateBar);
        Root.Loaded += (_, _) => { StartupTrace.Mark("Window content loaded (first frame)"); _ = LaunchTasksAsync(); };
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    // ------------------------------------------------------------------ installing and updating

    /// <summary>A few seconds after the window opens (so start-up is not slowed): report a failed update, offer to install HSA, look for a newer version.</summary>
    async Task LaunchTasksAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (File.Exists(UpdateApplier.ErrorFile))
            {
                var why = File.ReadAllText(UpdateApplier.ErrorFile); try { File.Delete(UpdateApplier.ErrorFile); } catch { }
                Toast(why, InfoBarSeverity.Error, 15);
            }
            if (!UpdateManager.Available) return;
            if (await OfferInstallAsync()) return;
            await App.Updates.CheckOnLaunchAsync();
        }
        catch (Exception ex) { AppLog.Write("Start-up tasks: " + ex); }
    }

    /// <summary>Returns true when HSA installed itself and is restarting from the install folder.</summary>
    async Task<bool> OfferInstallAsync()
    {
        for (int i = 0; i < 50 && !App.State.SettingsLoaded; i++) await Task.Delay(200);
        var loc = InstallLocations.Default;
        if (App.State.Settings.InstallPromptDismissed || SelfInstaller.IsRunningFromInstall(loc, Environment.ProcessPath)) return false;

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Install HP Smart Alternative?", DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Text =
                "Installing adds HSA to the Start menu and to Apps & features, and lets it check for new versions and update itself. It needs no administrator rights, and your scans and settings are not touched." },
            PrimaryButtonText = "Install", CloseButtonText = "Not now", SecondaryButtonText = "Don't ask again",
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary) { App.State.Settings.InstallPromptDismissed = true; App.State.SaveSettings(); return false; }
        if (result != ContentDialogResult.Primary) return false;
        return await InstallNowAsync();
    }

    public async Task<bool> InstallNowAsync()
    {
        try
        {
            var exe = await Task.Run(() => SelfInstaller.Install(InstallLocations.Default, Environment.ProcessPath!, AppState.Version));
            App.State.SaveSettings();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            Application.Current.Exit();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Install failed: " + ex);
            await Ui.MessageAsync(Root.XamlRoot, "HSA could not be installed", ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
                ? "HSA is already running from the install folder. Close that copy first, then try again."
                : ex.Message);
            return false;
        }
    }

    void RefreshUpdateBar()
    {
        var u = App.Updates; var v = u.Info?.Version.ToString(3);
        bool show = u.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready or UpdateState.Failed;
        UpdateBar.IsOpen = show;
        if (!show) return;

        UpdateBar.Severity = u.State == UpdateState.Failed ? InfoBarSeverity.Warning : u.State == UpdateState.Ready ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        UpdateProgress.Visibility = u.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = u.Progress;
        UpdateActionButton.Visibility = u.State == UpdateState.Downloading ? Visibility.Collapsed : Visibility.Visible;
        UpdateSkipLink.Visibility = UpdateLaterLink.Visibility = u.State == UpdateState.Downloading ? Visibility.Collapsed : Visibility.Visible;
        switch (u.State)
        {
            case UpdateState.Available:
                UpdateBar.Title = $"HSA {v} is available"; UpdateBar.Message = $"You have {AppState.Version}.";
                UpdateActionButton.Content = u.CanReplaceThisInstall ? "Download and install" : "Open the download page"; break;
            case UpdateState.Downloading:
                UpdateBar.Title = $"Downloading HSA {v}…"; UpdateBar.Message = $"{u.Progress:P0}"; break;
            case UpdateState.Ready:
                UpdateBar.Title = $"HSA {v} is ready"; UpdateBar.Message = "HSA closes and opens again in the new version. Your settings are kept.";
                UpdateActionButton.Content = "Restart and update"; break;
            case UpdateState.Failed:
                UpdateBar.Title = "The update did not download"; UpdateBar.Message = u.Message; UpdateActionButton.Content = "Try again"; break;
        }
        AutomationProperties.SetName(UpdateActionButton, (string)UpdateActionButton.Content);
    }

    async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        var u = App.Updates;
        switch (u.State)
        {
            case UpdateState.Available when !u.CanReplaceThisInstall: u.OpenReleasePage(); break;
            case UpdateState.Available or UpdateState.Failed: await u.DownloadAsync(); break;
            case UpdateState.Ready: if (!u.RestartAndUpdate()) Toast("HSA is in a folder it cannot change, so the download page was opened instead.", InfoBarSeverity.Warning); break;
        }
    }

    void UpdateNotes_Click(object sender, RoutedEventArgs e) => App.Updates.OpenReleasePage();
    void UpdateSkip_Click(object sender, RoutedEventArgs e) => App.Updates.SkipThisVersion();
    void UpdateLater_Click(object sender, RoutedEventArgs e) => App.Updates.Dismiss();

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
