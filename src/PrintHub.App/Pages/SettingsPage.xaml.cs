using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrintHub.App.Services;
using PrintHub.Core.Imaging;
using PrintHub.Core.Printing;
using PrintHub.Core.Settings;
using PrintHub.Core.Updates;
using PrintHub.Core.Usb;

namespace PrintHub.App.Pages;

public sealed partial class SettingsPage : Page
{
    bool _loading;

    public SettingsPage() => InitializeComponent();

    AppSettings S => App.State.Settings;

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        Ui.Fill(ThemeCombo, new[] { ("Use system setting", "System"), ("Light", "Light"), ("Dark", "Dark") }, S.Theme);
        Ui.Fill(RouteCombo, new[] { ("Automatic (driver if installed, otherwise direct)", PrintRoute.Auto), ("Always the Windows printer driver", PrintRoute.WindowsDriver), ("Always directly (IPP)", PrintRoute.DirectIpp) }, S.PrintRoute);
        UsbSwitch.IsOn = S.PreferUsb;
        OpenFolderCheck.IsChecked = S.OpenFolderAfterSave;
        SearchableCheck.IsChecked = S.SearchablePdf;
        _loading = false;

        FolderBox.Text = S.EffectiveSaveFolder;
        OcrNote.Text = OcrService.IsAvailable
            ? "Text recognition is available: " + string.Join(", ", OcrService.Languages)
            : "Text recognition is not available: install a language with OCR support in Windows Settings > Time & language > Language & region.";
        AboutText.Text = $"HP Smart Alternative (HSA) {AppState.Version}\nAn alternative to HP Smart for Windows. Works with any printer that supports IPP / eSCL or has a Windows driver, over network and USB.\nSettings: {SettingsStore.FilePath}";
        BuildSaved();
        RescanUsb();

        _loading = true;
        UpdateCheckSwitch.IsOn = S.CheckForUpdates; UpdateAutoSwitch.IsOn = S.AutoDownloadUpdates;
        _loading = false;
        App.Updates.Changed += OnUpdateChanged;
        ShowUpdateStatus();
    }

    void Page_Unloaded(object sender, RoutedEventArgs e) => App.Updates.Changed -= OnUpdateChanged;
    void OnUpdateChanged() => DispatcherQueue.TryEnqueue(ShowUpdateStatus);

    void ShowUpdateStatus()
    {
        bool can = UpdateManager.Available;
        UpdateCheckButton.IsEnabled = can && App.Updates.State is not (UpdateState.Checking or UpdateState.Downloading);
        UpdateStatus.Text = !can ? "Updates are switched off while HSA runs from a development or test folder."
            : App.Updates.Message.Length > 0 ? App.Updates.Message : $"HSA {AppState.Version}. Last checked: {(S.LastUpdateCheck is { } t ? t.ToLocalTime().ToString("g") : "never")}.";

        var loc = InstallLocations.Default; var here = Environment.ProcessPath;
        bool fromInstall = SelfInstaller.IsRunningFromInstall(loc, here), installed = SelfInstaller.IsInstalled(loc);
        InstallStatus.Text = fromInstall ? $"HSA is installed ({loc.InstallDir}). New versions replace it in place."
            : installed ? $"HSA is installed at {loc.InstallDir}. This window is running from {here}."
            : $"HSA is not installed: it is running from {here}.";
        InstallButton.Visibility = fromInstall ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = installed ? "Reinstall HSA from this copy" : "Install HSA on this computer";
        InstallButton.IsEnabled = can;
    }

    void UpdateCheck_Toggled(object sender, RoutedEventArgs e) { if (_loading) return; S.CheckForUpdates = UpdateCheckSwitch.IsOn; Save(); }
    void UpdateAuto_Toggled(object sender, RoutedEventArgs e) { if (_loading) return; S.AutoDownloadUpdates = UpdateAutoSwitch.IsOn; Save(); }
    async void UpdateCheckNow_Click(object sender, RoutedEventArgs e) => await App.Updates.CheckAsync(manual: true);

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.ConfirmAsync(XamlRoot, "Install HSA?", "HSA copies itself to your user folder, adds a Start menu shortcut and an Apps & features entry, then restarts from there. Your scans and settings are not touched.", "Install"))
            await App.Window.InstallNowAsync();
    }

    void Save() { if (!_loading) App.State.SaveSettings(); }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Theme = Ui.Selected<string>(ThemeCombo) ?? "System"; Save(); App.Window.ApplyTheme(S.Theme);
    }

    void Route_Changed(object sender, SelectionChangedEventArgs e) { if (_loading) return; S.PrintRoute = Ui.Selected<PrintRoute>(RouteCombo); Save(); }

    async void Usb_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.PreferUsb = UsbSwitch.IsOn; Save();
        if (App.State.Current is { } d) await App.State.SelectAsync(d);
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.OpenFolderAfterSave = OpenFolderCheck.IsChecked == true; S.SearchablePdf = SearchableCheck.IsChecked == true; Save();
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = await Ui.PickFolderAsync();
        if (f is null) return;
        S.SaveFolder = f; Save(); FolderBox.Text = f;
    }

    void DefaultFolder_Click(object sender, RoutedEventArgs e) { S.SaveFolder = null; Save(); FolderBox.Text = S.EffectiveSaveFolder; }
    void OpenFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenFile(S.EffectiveSaveFolder);
    void OpenLogs_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(AppLog.Folder); Ui.OpenFile(AppLog.Folder); }
    void OpenSettings_Click(object sender, RoutedEventArgs e) => Ui.OpenFile(Path.GetDirectoryName(SettingsStore.FilePath)!);

    // ------------------------------------------------------------------ clean up printers and drivers

    void CleanupBusy(bool busy, string text = "") { RemovePrintersButton.IsEnabled = RemoveDriversButton.IsEnabled = !busy; CleanupRing.IsActive = busy; CleanupStatus.Text = text; }

    async void RemovePrinters_Click(object sender, RoutedEventArgs e)
    {
        CleanupBusy(true, "Looking at the installed printers…");
        try
        {
            var plan = PrinterCleanup.PlanPrinters(SpoolerPrinters.List());
            if (plan.Remove.Count == 0)
            {
                CleanupBusy(false, "There are no printers to remove.");
                await Ui.MessageAsync(XamlRoot, "Nothing to remove", "Windows has no printers installed apart from its built-in ones (" + string.Join(", ", plan.Keep) + ").");
                return;
            }
            var exemptBefore = new HashSet<string>(S.KeepPrinters, StringComparer.OrdinalIgnoreCase);
            var items = plan.Remove.Select(n => new Ui.ChecklistItem(n, n, !exemptBefore.Contains(n))).ToList();
            string message = "Tick the printers to remove from Windows, together with their saved settings: the entries Windows keeps for them in the registry " +
                             "(HKEY_CURRENT_USER\\Printers: ConvertUserDevModesCount, DevModePerUser and DevModes2), the leftover device entries Windows Settings lists them from, and any copy saved in HSA. " +
                             "Untick a printer to keep it: it is left exactly as it is, and HSA remembers that for next time. " +
                             (plan.Keep.Count > 0 ? $"Windows' built-in printers always stay ({string.Join(", ", plan.Keep)}). " : "") +
                             "The drivers stay installed (use “Uninstall printer drivers” for them). This cannot be undone: removed printers have to be added again.";
            var chosen = await Ui.ConfirmChecklistAsync(XamlRoot, "Which printers should be removed?", message, items, "Remove {0} printers");
            if (chosen is null) { CleanupBusy(false, "Nothing was removed."); return; }

            // remember what was exempted, before anything else can go wrong
            S.KeepPrinters = PrinterCleanup.UpdateExemptions(S.KeepPrinters, items.Where(i => i.Ticked).Select(i => i.Key), chosen, plan.Remove); Save();

            CleanupBusy(true, "Waiting for administrator permission…");
            PrinterCleanup.Outcome outcome;
            try { outcome = await PrinterCleanup.RunElevatedAsync(result => PrinterCleanup.BuildRemovePrintersScript(chosen, result)); }
            catch (OperationCanceledException) { CleanupBusy(false, "Administrator permission was not given. Nothing was removed."); return; }

            CleanupBusy(true, "Cleaning up the saved settings…");
            var remaining = SpoolerPrinters.List().Select(p => p.Name).ToList();             // what really still exists (exempt, built-in, or refused), whatever the script reported
            // saved first, so the entries can be put back by double-clicking the file
            string? backup = PrinterCleanup.BackupRegistry(Path.Combine(PrintHub.Core.Settings.AppPaths.DataDir, "registry-backups"));
            int registry = PrinterCleanup.CleanRegistry(remaining);
            // printers saved in HSA: gone, unless one is a printer that was exempted
            var keepNames = new HashSet<string>(remaining.Concat(S.KeepPrinters), StringComparer.OrdinalIgnoreCase);
            var clearedSaved = S.Printers.Where(sp => !keepNames.Contains(sp.Name)).ToList();
            S.Printers.RemoveAll(sp => clearedSaved.Contains(sp));
            if (S.SelectedPrinterId is { } sel && clearedSaved.Any(sp => sp.Id == sel)) S.SelectedPrinterId = null;
            Save();
            var gone = new HashSet<string>(outcome.Done, StringComparer.OrdinalIgnoreCase);
            var cur = App.State.Current;
            bool currentGone = cur is not null && ((cur.SpoolerName is not null && gone.Contains(cur.SpoolerName)) || clearedSaved.Any(sp => sp.Id == cur.Id));
            App.State.Devices.RemoveAll(d => (d.SpoolerName is not null && gone.Contains(d.SpoolerName)) || clearedSaved.Any(sp => sp.Id == d.Id));
            BuildSaved();
            if (currentGone) await App.State.ForgetPrinterAsync();
            await App.State.DiscoverAsync();

            int kept = plan.Remove.Count - chosen.Count;
            string summary = $"Removed {outcome.Done.Count} of {chosen.Count} printers" + (kept > 0 ? $" ({kept} kept as you chose)" : "") +
                             $". Cleaned {registry} saved setting{(registry == 1 ? "" : "s")} in the registry" + (outcome.Devices.Count > 0 ? $", and {outcome.Devices.Count} leftover device entr{(outcome.Devices.Count == 1 ? "y" : "ies")} so Windows Settings no longer lists them" : "") + (backup is not null ? $" (a copy of the registry key is in {backup})" : "") + (clearedSaved.Count > 0 ? $" and {clearedSaved.Count} printer{(clearedSaved.Count == 1 ? "" : "s")} saved in HSA." : ".");
            AppLog.Write("Printer clean-up: " + summary + (outcome.Failed.Count > 0 ? " Failed: " + string.Join("; ", outcome.Failed.Select(f => f.Name + " (" + f.Reason + ")")) : ""));
            CleanupBusy(false, summary);
            if (outcome.Failed.Count > 0 || outcome.DeviceFailed.Count > 0)
                await Ui.MessageAsync(XamlRoot, "Some printers could not be removed", string.Join("\n", outcome.Failed.Concat(outcome.DeviceFailed).Select(f => $"{f.Name}: {f.Reason}")) + "\n\nA printer with a job stuck in its queue is the usual reason: switch the printer off, clear the queue and try again.");
        }
        catch (Exception ex) { AppLog.Write("Printer clean-up: " + ex); CleanupBusy(false, "That did not work: " + ex.Message); }
    }

    async void RemoveDrivers_Click(object sender, RoutedEventArgs e)
    {
        CleanupBusy(true, "Looking at the installed drivers…");
        try
        {
            var plan = PrinterCleanup.PlanDrivers(await PrinterCleanup.ListDriversAsync());
            if (plan.Remove.Count == 0)
            {
                CleanupBusy(false, "There are no manufacturer drivers to remove.");
                await Ui.MessageAsync(XamlRoot, "Nothing to remove", "Windows has no printer drivers from HP, Epson, Canon, Brother or other manufacturers installed.");
                return;
            }
            // a driver a printer still uses is offered unticked: Windows would refuse it, and a printer that was kept (exempt) keeps its driver
            var usedBy = SpoolerPrinters.List().GroupBy(p => p.Driver, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(p => p.Name).ToList(), StringComparer.OrdinalIgnoreCase);
            var exemptBefore = new HashSet<string>(S.KeepDrivers, StringComparer.OrdinalIgnoreCase);
            var items = plan.Remove.Select(n => usedBy.TryGetValue(n, out var users)
                ? new Ui.ChecklistItem(n, $"{n}   (used by {(users.Count == 1 ? users[0] : users.Count + " printers")})", false)
                : new Ui.ChecklistItem(n, n, !exemptBefore.Contains(n))).ToList();
            string message = "Tick the printer drivers to remove from Windows and from its driver store. Microsoft's own and generic drivers always stay. " +
                             "A driver that an installed printer uses is not ticked (Windows would refuse it): remove that printer first, or keep both. " +
                             "Untick a driver to keep it: HSA remembers that for next time. To use a printer again afterwards, install its driver (the “Download drivers” link on the maintenance pages).";
            var chosen = await Ui.ConfirmChecklistAsync(XamlRoot, "Which drivers should be uninstalled?", message, items, "Uninstall {0} drivers");
            if (chosen is null) { CleanupBusy(false, "Nothing was removed."); return; }
            S.KeepDrivers = PrinterCleanup.UpdateExemptions(S.KeepDrivers, items.Where(i => i.Ticked).Select(i => i.Key), chosen, plan.Remove); Save();

            CleanupBusy(true, "Waiting for administrator permission…");
            PrinterCleanup.Outcome outcome;
            try { outcome = await PrinterCleanup.RunElevatedAsync(result => PrinterCleanup.BuildRemoveDriversScript(chosen, result)); }
            catch (OperationCanceledException) { CleanupBusy(false, "Administrator permission was not given. Nothing was removed."); return; }

            int kept = plan.Remove.Count - chosen.Count;
            string summary = $"Uninstalled {outcome.Done.Count} of {chosen.Count} drivers" + (kept > 0 ? $" ({kept} left as you chose or because a printer uses them)." : ".");
            AppLog.Write("Driver clean-up: " + summary + (outcome.Failed.Count > 0 ? " Failed: " + string.Join("; ", outcome.Failed.Select(f => f.Name + " (" + f.Reason + ")")) : ""));
            CleanupBusy(false, summary);
            if (outcome.Failed.Count > 0)
                await Ui.MessageAsync(XamlRoot, "Some drivers could not be removed", string.Join("\n", outcome.Failed.Select(f => $"{f.Name}: {f.Reason}")) + "\n\nA driver that an installed printer still uses cannot be removed: remove the printer first.");
        }
        catch (Exception ex) { AppLog.Write("Driver clean-up: " + ex); CleanupBusy(false, "That did not work: " + ex.Message); }
    }

    void BuildSaved()
    {
        SavedPanel.Children.Clear();
        SavedNone.Visibility = S.Printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in S.Printers.ToList())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{p.Name}   ({p.Address})", VerticalAlignment = VerticalAlignment.Center });
            var rm = new Button { Content = "Remove" };
            rm.Click += (_, _) =>
            {
                S.Printers.Remove(p); Save();
                App.State.Devices.RemoveAll(d => d.Id == p.Id && !d.HasUsbHttp && d.SpoolerName is null);
                BuildSaved(); _ = App.State.DiscoverAsync();
            };
            Grid.SetColumn(rm, 1); row.Children.Add(rm);
            SavedPanel.Children.Add(row);
        }
    }

    void RescanUsb_Click(object sender, RoutedEventArgs e) => RescanUsb();

    void RescanUsb()
    {
        try
        {
            var all = UsbDeviceScanner.Scan(presentOnly: false).Where(i => i.HttpKind != UsbHttpKind.None).OrderByDescending(i => i.Present).ToList();
            UsbBox.Text = all.Count == 0
                ? "No printers with a USB web interface were found. Connect the printer with a USB cable and switch it on."
                : string.Join(Environment.NewLine, all.Select(i => (i.Present ? "● " : "○ ") + i.Describe())) +
                  Environment.NewLine + Environment.NewLine + "● connected now   ○ remembered by Windows but not connected";
        }
        catch (Exception ex) { UsbBox.Text = "Could not read USB devices: " + ex.Message; }
    }
}
