using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PrintHub.App.Services;

namespace PrintHub.App.Pages;

/// <summary>The printer's embedded web server (EWS), shown in-app. Works for network printers and, through the loopback proxy, USB ones.</summary>
public sealed partial class WebPage : Page
{
    Uri? _target;

    public WebPage() => InitializeComponent();

    async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged += OnChanged;
        await LoadAsync();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        App.State.CurrentChanged -= OnChanged;
        try { Web.Close(); } catch { }
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(async () => await LoadAsync());

    async Task LoadAsync()
    {
        var s = App.State;
        _target = s.Session?.WebUri ?? s.Current?.WebUri;
        Notice.IsOpen = false;

        if (s.Current is null) { Show("Choose a printer to open its web page."); return; }
        if (s.Connecting) { Show("Connecting to the printer…"); return; }
        if (_target is null)
        {
            Show(s.Current.HasUsbHttp ? "The printer's USB web interface did not answer. Try unplugging and re-plugging the USB cable, then press Refresh."
                                      : "This printer has no web page address. If it is on your network, add it by IP address using the button at the top.");
            return;
        }

        try
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(PrintHub.Core.Settings.AppPaths.DataDir, "webview"));
            await Web.EnsureCoreWebView2Async();
            Web.CoreWebView2.ServerCertificateErrorDetected -= OnCertError;
            Web.CoreWebView2.ServerCertificateErrorDetected += OnCertError;
            Web.CoreWebView2.NavigationCompleted -= OnNavigated;
            Web.CoreWebView2.NavigationCompleted += OnNavigated;
            Web.CoreWebView2.SourceChanged -= OnSource;
            Web.CoreWebView2.SourceChanged += OnSource;
            Placeholder.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            Web.CoreWebView2.Navigate(_target.ToString());
            if (s.Session?.ViaUsb == true)
            {
                Notice.Severity = InfoBarSeverity.Informational;
                Notice.Title = "Connected over USB";
                Notice.Message = "This page is served by the printer through the USB cable (via a private local connection). It is not visible to other computers.";
                Notice.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WebView2: " + ex);
            Show("The embedded browser (Microsoft Edge WebView2) could not start: " + ex.Message + "\nYou can still open the page in your normal browser.");
        }
    }

    void Show(string text)
    {
        Placeholder.Text = text; Placeholder.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Collapsed; AddressBox.Text = _target?.ToString() ?? "";
    }

    // Printers use self-signed certificates. Only accept them for the printer's own (private) address.
    void OnCertError(CoreWebView2 sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        if (Uri.TryCreate(args.RequestUri, UriKind.Absolute, out var u) && _target is not null && u.Host == _target.Host)
            args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    void OnSource(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args) => DispatcherQueue.TryEnqueue(() => AddressBox.Text = sender.Source);

    void OnNavigated(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BackButton.IsEnabled = sender.CanGoBack; ForwardButton.IsEnabled = sender.CanGoForward;
            if (!args.IsSuccess)
            {
                Notice.Severity = InfoBarSeverity.Warning; Notice.Title = "The page didn't load";
                Notice.Message = $"{args.WebErrorStatus}. Check that the printer is on, then press Reload.";
                Notice.IsOpen = true;
            }
            else if (App.State.Session?.ViaUsb != true) Notice.IsOpen = false;
        });
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    void Forward_Click(object sender, RoutedEventArgs e) { if (Web.CanGoForward) Web.GoForward(); }
    async void Reload_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2 is not null && Web.Visibility == Visibility.Visible) Web.Reload(); else await LoadAsync(); }

    async void External_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;
        if (App.State.Session?.ViaUsb == true)
            await Ui.MessageAsync(XamlRoot, "Opening in your browser", "The USB connection is only available while HSA is running. The page will open in your default browser using the local address " + _target + ", which only works on this PC.");
        await Windows.System.Launcher.LaunchUriAsync(_target);
    }
}
