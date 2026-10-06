using System.Diagnostics;

namespace PrintHub.Core.Updates;

/// <summary>
/// Puts a downloaded, verified update in place of the running program. A program cannot overwrite itself while it runs, so the new exe is started in
/// "apply" mode: it waits for the old process to exit, swaps itself in (keeping the old file until the swap worked, and putting it back if it did
/// not), starts the installed copy again and exits.
/// </summary>
public static class UpdateApplier
{
    public const string ApplyArg = "--apply-update";
    public static string ErrorFile => Path.Combine(Settings.AppPaths.DataDir, "update-error.txt");

    /// <summary>True when HSA can replace <paramref name="targetExe"/>: it is a real HSA exe in a folder the user may write to.</summary>
    public static bool CanReplace(string? targetExe)
    {
        if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe) || !string.Equals(Path.GetExtension(targetExe), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(Path.GetFileNameWithoutExtension(targetExe), "dotnet", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(targetExe)!, $".hsa-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "x"); File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts <paramref name="newExe"/> in apply mode. The caller must exit straight afterwards so the old file is free.</summary>
    public static void StartApply(string newExe, string targetExe)
    {
        var psi = new ProcessStartInfo(newExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(newExe)! };
        psi.ArgumentList.Add(ApplyArg); psi.ArgumentList.Add(targetExe); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>Entry point for "HSA.exe --apply-update &lt;target&gt; &lt;pid&gt;". Returns true when this process was an apply run (and should now exit).</summary>
    public static bool RunIfRequested(string[] commandLine)
    {
        int i = Array.IndexOf(commandLine, ApplyArg);
        if (i < 0 || i + 2 >= commandLine.Length) return false;
        var target = commandLine[i + 1]; _ = int.TryParse(commandLine[i + 2], out var pid);
        var source = Environment.ProcessPath ?? "";
        var error = Apply(source, target, pid, TimeSpan.FromSeconds(60), 40, TimeSpan.FromMilliseconds(500), relaunch: true);
        try { if (error is null) File.Delete(ErrorFile); else { Directory.CreateDirectory(Path.GetDirectoryName(ErrorFile)!); File.WriteAllText(ErrorFile, error); } } catch { }
        return true;
    }

    /// <summary>Swaps <paramref name="source"/> in over <paramref name="target"/>. Returns null on success, or what went wrong (the old file is then back in place).</summary>
    public static string? Apply(string source, string target, int waitForPid, TimeSpan waitForExit, int retries, TimeSpan retryDelay, bool relaunch)
    {
        string? error = null;
        try
        {
            if (waitForPid > 0)
            {
                try { using var p = Process.GetProcessById(waitForPid); if (!p.WaitForExit(waitForExit)) return Finish("The old version did not close in time, so the update was not installed.", target, relaunch); }
                catch (ArgumentException) { /* already gone */ }
            }

            var old = target + ".old";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Move(target, old, overwrite: true);
                    try { File.Copy(source, target, overwrite: true); }
                    catch { try { if (File.Exists(old)) File.Move(old, target, overwrite: true); } catch { } throw; }
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= retries) { error = "The update could not be installed: " + ex.Message; break; }
                    Thread.Sleep(retryDelay);
                }
            }
            if (error is null)
            {
                try { File.Delete(target + ":Zone.Identifier"); } catch { }   // the user chose to update: no "are you sure?" prompt on the next start
                try { File.Delete(target + ".old"); } catch { }
            }
        }
        catch (Exception ex) { error = "The update could not be installed: " + ex.Message; }
        return Finish(error, target, relaunch);
    }

    static string? Finish(string? error, string target, bool relaunch)
    {
        if (relaunch && File.Exists(target))
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! }); }
            catch (Exception ex) { error ??= "The update was installed but HSA could not be started again: " + ex.Message; }
        }
        return error;
    }

    /// <summary>Removes downloads and leftovers from earlier updates. A pending download of a newer version than <paramref name="current"/> is kept.</summary>
    public static void CleanUp(Version current, string? runningExe, string? folder = null)
    {
        try
        {
            if (runningExe is not null) File.Delete(runningExe + ".old");
            folder ??= UpdateService.UpdatesFolder;
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder))
            {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^HSA-(\d+\.\d+\.\d+)-win-x64\.exe$");
                if (m.Success && Version.TryParse(m.Groups[1].Value, out var v) && UpdateService.IsNewer(v, current)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
