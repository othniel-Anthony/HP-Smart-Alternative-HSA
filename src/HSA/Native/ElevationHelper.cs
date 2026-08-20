using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace HSA.Native;

/// <summary>
/// Helpers for the UAC / integrity-level story.
///
/// The app runs asInvoker. Privileged actions (driver install/remove, deleting printer
/// packages, modifying spooler state on a system printer) are spawned in a child process
/// with runas-verb elevation. The child re-enters the same executable with a special
/// --elevated-arg argument so the user can see what is being asked.
///
/// This is a deliberate per-action model rather than running the whole UI elevated.
/// </summary>
public static class ElevationHelper
{
    public const string ElevationFlag = "--hpsa-elevated";
    public const string Verb = "runas";

    public static bool IsRunAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relaunches the current process with elevation, forwarding the same arguments plus the
    /// elevation flag. Returns the new PID, or null if the user declined UAC.
    /// </summary>
    public static int? RelaunchElevated(string[] extraArgs)
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName
                  ?? Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot determine executable path.");

        var args = new List<string> { ElevationFlag };
        args.AddRange(extraArgs);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', args.Select(QuoteIfNeeded)),
            UseShellExecute = true,
            Verb = Verb,
            CreateNoWindow = false
        };
        try
        {
            var p = Process.Start(psi);
            return p?.Id;
        }
        catch
        {
            return null;
        }
    }

    private static string QuoteIfNeeded(string s) =>
        s.Contains(' ') ? $"\"{s}\"" : s;
}
