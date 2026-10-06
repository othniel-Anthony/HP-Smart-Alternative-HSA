using System.Diagnostics;
using Microsoft.Win32;

namespace PrintHub.Core.Updates;

/// <summary>Where an installation lives. The defaults are the same place the PowerShell installer in the zip uses, so both are recognised as one installation.</summary>
public sealed record InstallLocations(string InstallDir, string StartMenuDir, string RegistryKey)
{
    public const string ShortcutName = "HP Smart Alternative (HSA).lnk";
    public string ExePath => Path.Combine(InstallDir, "HSA.exe");
    public string ShortcutPath => Path.Combine(StartMenuDir, ShortcutName);

    /// <summary>Test hook: put the install folder, Start menu shortcut and registry entry under this folder / a private key instead of the real ones.</summary>
    public const string TestRootEnvVar = "HSA_INSTALL_ROOT";

    public static InstallLocations Default { get; } = Environment.GetEnvironmentVariable(TestRootEnvVar) is { Length: > 0 } test
        ? new(Path.Combine(test, "Programs", "HP Smart Alternative"), Path.Combine(test, "Start Menu"),
              @"Software\HSA-install-test\" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(test.ToLowerInvariant())))[..16])
        : new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HP Smart Alternative"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HPSmartAlternative");
}

/// <summary>
/// Installs HSA for the current user, with no administrator rights and no extra installer program: the exe copies itself to the install folder,
/// adds a Start menu shortcut and an "Apps &amp; features" entry, and from then on can update itself there.
/// </summary>
public static class SelfInstaller
{
    public const string UninstallArg = "--uninstall";

    public static bool IsInstalled(InstallLocations loc)
    {
        if (!File.Exists(loc.ExePath)) return false;
        using var k = Registry.CurrentUser.OpenSubKey(loc.RegistryKey);
        return k is not null;
    }

    public static bool IsRunningFromInstall(InstallLocations loc, string? exePath) =>
        exePath is not null && string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(loc.ExePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies <paramref name="sourceExe"/> into the install folder and registers it. Returns the installed exe's path.</summary>
    public static string Install(InstallLocations loc, string sourceExe, string version)
    {
        Directory.CreateDirectory(loc.InstallDir);
        if (!IsRunningFromInstall(loc, sourceExe))
        {
            var tmp = loc.ExePath + ".new";
            File.Copy(sourceExe, tmp, overwrite: true);
            File.Move(tmp, loc.ExePath, overwrite: true);   // fails (clearly) when another copy is running from there
        }
        try { File.Delete(loc.ExePath + ":Zone.Identifier"); } catch { }   // you chose to install it: no "are you sure?" prompt from the shortcut

        Directory.CreateDirectory(loc.StartMenuDir);
        CreateShortcut(loc.ShortcutPath, loc.ExePath, loc.InstallDir);

        using var key = Registry.CurrentUser.CreateSubKey(loc.RegistryKey);
        key.SetValue("DisplayName", "HP Smart Alternative (HSA)");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "HSA");
        key.SetValue("InstallLocation", loc.InstallDir);
        key.SetValue("DisplayIcon", loc.ExePath);
        key.SetValue("UninstallString", $"\"{loc.ExePath}\" {UninstallArg}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return loc.ExePath;
    }

    /// <summary>Removes the shortcut and the "Apps &amp; features" entry, and the install folder (from a detached command when the running program is inside it).</summary>
    public static void Uninstall(InstallLocations loc, bool removeData, string? dataDir, bool deleteFolderNow = false)
    {
        try { File.Delete(loc.ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(loc.RegistryKey, throwOnMissingSubKey: false); } catch { }
        if (removeData && dataDir is not null) try { Directory.Delete(dataDir, true); } catch { }

        if (deleteFolderNow) { try { Directory.Delete(loc.InstallDir, true); } catch { } return; }
        // the running exe lives in the folder: remove it a moment after this process has ended
        var cmd = new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{loc.InstallDir}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        try { Process.Start(cmd); } catch { }
    }

    static void CreateShortcut(string lnkPath, string target, string workingDir)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available, so the Start menu shortcut could not be made.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target; lnk.WorkingDirectory = workingDir; lnk.IconLocation = target + ",0"; lnk.Description = "Print, scan and copy with your printer";
            lnk.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
