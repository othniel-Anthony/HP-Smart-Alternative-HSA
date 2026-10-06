using System.Runtime.InteropServices;
using PrintHub.Core.Updates;

namespace PrintHub.App.Services;

/// <summary>Command-line modes that run without opening the window: finishing an update, and uninstalling from "Apps &amp; features".</summary>
static class HeadlessModes
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    const uint MB_YESNO = 0x4, MB_OK = 0x0, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40;
    const int IDYES = 6;
    const string Caption = "HP Smart Alternative (HSA)";

    /// <summary>Returns true when the command line asked for one of these modes (the caller then exits).</summary>
    public static bool RunIfRequested(string[] args)
    {
        if (UpdateApplier.RunIfRequested(args)) return true;
        if (args.Contains(SelfInstaller.UninstallArg, StringComparer.OrdinalIgnoreCase)) { Uninstall(); return true; }
        return false;
    }

    static void Uninstall()
    {
        if (MessageBoxW(IntPtr.Zero, "Remove HP Smart Alternative (HSA) from this computer?\n\nYour scans and settings are kept.", Caption, MB_YESNO | MB_ICONQUESTION) != IDYES) return;
        // stop a running copy that is installed here (not this process)
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("HSA"))
        {
            try { if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, InstallLocations.Default.ExePath, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch { }
        }
        SelfInstaller.Uninstall(InstallLocations.Default, removeData: false, dataDir: null);
        MessageBoxW(IntPtr.Zero, "HP Smart Alternative (HSA) was removed.", Caption, MB_OK | MB_ICONINFORMATION);
    }
}
