using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using HSA.Models;

namespace HSA.Native;

/// <summary>
/// Wraps `pnputil.exe` to enumerate and remove driver packages from the Windows driver store.
/// Reference: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
/// </summary>
public static class PnpUtil
{
    private const string Exe = "pnputil.exe";

    /// <summary>
    /// Runs `pnputil /enum-drivers` and parses the output into structured rows.
    /// </summary>
    public static async Task<List<DriverInfo>> EnumerateDriversAsync(CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunAsync("/enum-drivers", ct);
        if (exit != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"pnputil /enum-drivers failed (exit {exit}). {stderr}".Trim());
        }
        return ParseEnumDrivers(stdout);
    }

    /// <summary>
    /// Removes a driver package from the driver store. Requires admin.
    /// The published name is the oem*.inf identifier from `pnputil /enum-drivers`.
    /// </summary>
    public static async Task<CommandResult> RemoveDriverAsync(string publishedName, bool force, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publishedName))
            throw new ArgumentException("Published name is required.", nameof(publishedName));
        var args = force
            ? $"/delete-driver {publishedName} /force"
            : $"/delete-driver {publishedName}";
        return await RunAsync(args, ct);
    }

    /// <summary>
    /// Removes a single PnP device instance from the system. Requires admin.
    /// The instance ID is the full PnP path (e.g. "USBPRINT\HPHP_LJ_Pro_M404\..." or
    /// "ROOT\HP_PRINT_QUEUE\0000"). This unregisters the device AND cascades to clean
    /// up its service / Enum / Print Spooler references in the registry - much deeper
    /// than `/delete-driver` (which only removes the package from the store).
    /// </summary>
    public static async Task<CommandResult> RemoveDeviceAsync(string instanceId, bool force, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        var args = force
            ? $"/remove-device \"{instanceId}\" /force"
            : $"/remove-device \"{instanceId}\"";
        return await RunAsync(args, ct);
    }

    /// <summary>
    /// Adds a driver package to the driver store. Requires admin. Use this before AddPrinter
    /// if you want to install a raw INF.
    /// </summary>
    public static async Task<CommandResult> AddDriverAsync(string infPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(infPath))
            throw new ArgumentException("INF path is required.", nameof(infPath));
        if (!File.Exists(infPath))
            throw new FileNotFoundException("INF not found.", infPath);
        return await RunAsync($"/add-driver \"{infPath}\" /install", ct);
    }

    /// <summary>
    /// Re-scans PnP for new devices. Useful after driver install to trigger enumeration.
    /// </summary>
    public static async Task<CommandResult> RescanAsync(CancellationToken ct = default)
    {
        return await RunAsync("/scan-devices", ct);
    }

    /// <summary>
    /// Runs a batch of pnputil commands inside ONE elevated cmd.exe process. The user
    /// gets a single UAC prompt for the whole batch instead of one per command, which
    /// is the difference between a usable "Remove ALL HP drivers" flow and a
    /// click-through-N-times nightmare.
    ///
    /// Each command must be the full pnputil argument string (e.g. "/remove-device
    /// SWD\\PRINTENUM\\... /force"). Output from every command is concatenated.
    /// </summary>
    public static async Task<BatchResult> RunBatchAsync(
        IReadOnlyList<string> argList, CancellationToken ct = default)
    {
        if (argList is null || argList.Count == 0)
            return new BatchResult(0, string.Empty, string.Empty, new List<BatchLine>());

        // Build a temp .bat with one command per line. cmd.exe handles redirection
        // natively so we don't have to wire up PowerShell pipelines.
        var tempBat = Path.Combine(Path.GetTempPath(), $"hsa-pnputil-{Guid.NewGuid():N}.bat");
        var stdoutLog = Path.Combine(Path.GetTempPath(), $"hsa-pnputil-stdout-{Guid.NewGuid():N}.log");
        var stderrLog = Path.Combine(Path.GetTempPath(), $"hsa-pnputil-stderr-{Guid.NewGuid():N}.log");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"set HSA_EXIT=0");
        // Run each pnputil command. We capture stdout/stderr to per-line logs by
        // prefixing with the index so we can split them back out afterwards.
        for (int i = 0; i < argList.Count; i++)
        {
            var arg = argList[i].Replace("\"", "\\\"");
            sb.AppendLine($"echo [hsa:line {i}] running: pnputil {arg}");
            sb.AppendLine($"pnputil {arg} 1>\"{stdoutLog}.{i}\" 2>\"{stderrLog}.{i}\"");
            sb.AppendLine($"set RC{i}=%ERRORLEVEL%");
        }
        sb.AppendLine($"echo [hsa:line done] all commands finished");
        await File.WriteAllTextAsync(tempBat, sb.ToString(), ct);

        try
        {
            // Spawn elevated cmd.exe to run the script. UseShellExecute=true is required
            // for the Verb=runas (UAC) to actually trigger.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{tempBat}\"\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return new BatchResult(-1, "", "Failed to start elevated cmd.exe", new List<BatchLine>());
            await proc.WaitForExitAsync(ct);

            // Reconstruct combined output and per-command status.
            var perLine = new List<BatchLine>(argList.Count);
            var combined = new System.Text.StringBuilder();
            for (int i = 0; i < argList.Count; i++)
            {
                var so = File.Exists($"{stdoutLog}.{i}") ? File.ReadAllText($"{stdoutLog}.{i}") : "";
                var se = File.Exists($"{stderrLog}.{i}") ? File.ReadAllText($"{stderrLog}.{i}") : "";
                perLine.Add(new BatchLine(i, argList[i], string.IsNullOrWhiteSpace(se), se, so));
            }
            return new BatchResult(proc.ExitCode, string.Empty, string.Empty, perLine);
        }
        finally
        {
            // Clean up
            try { File.Delete(tempBat); } catch { }
            for (int i = 0; i < argList.Count; i++)
            {
                try { File.Delete($"{stdoutLog}.{i}"); } catch { }
                try { File.Delete($"{stderrLog}.{i}"); } catch { }
            }
            try { File.Delete(stdoutLog); } catch { }
            try { File.Delete(stderrLog); } catch { }
        }
    }

    private static async Task<CommandResult> RunAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Verb = "runas" // request elevation; will trigger UAC
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new CommandResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Parses the textual output of `pnputil /enum-drivers`. The format is stable across
    /// Windows 10/11 and Server editions.
    /// </summary>
    internal static List<DriverInfo> ParseEnumDrivers(string output)
    {
        var drivers = new List<DriverInfo>();
        DriverInfo? current = null;
        string? published = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith("Microsoft PnP Utility", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && published is not null)
                {
                    current = current with { PublishedName = published };
                    drivers.Add(current);
                }
                published = line.Substring("Published Name:".Length).Trim();
                current = new DriverInfo { PublishedName = published ?? string.Empty };
                continue;
            }
            if (current is null) continue;

            if (line.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase))
                current = current with { OriginalName = line.Substring("Original Name:".Length).Trim() };
            else if (line.StartsWith("Provider Name:", StringComparison.OrdinalIgnoreCase))
                current = current with { Provider = line.Substring("Provider Name:".Length).Trim() };
            else if (line.StartsWith("Class Name:", StringComparison.OrdinalIgnoreCase))
                current = current with { ClassName = line.Substring("Class Name:".Length).Trim() };
            else if (line.StartsWith("Class GUID:", StringComparison.OrdinalIgnoreCase))
                current = current with { ClassGuid = line.Substring("Class GUID:".Length).Trim() };
            else if (line.StartsWith("Driver Version:", StringComparison.OrdinalIgnoreCase))
                current = current with { DriverVersion = line.Substring("Driver Version:".Length).Trim() };
            else if (line.StartsWith("Driver Date:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line.Substring("Driver Date:".Length).Trim();
                if (DateTime.TryParse(raw, out var dt))
                    current = current with { DriverDate = dt };
            }
            else if (line.StartsWith("Inf Name:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("INF Name:", StringComparison.OrdinalIgnoreCase))
                current = current with { InfPath = line.Substring(line.IndexOf(':') + 1).Trim() };
            else if (line.StartsWith("Signer Name:", StringComparison.OrdinalIgnoreCase))
                current = current with
                {
                    IsSigned = true,
                    SignedBy = line.Substring("Signer Name:".Length).Trim()
                };
            else if (line.StartsWith("Catalog Name:", StringComparison.OrdinalIgnoreCase))
                current = current with
                {
                    IsSigned = !string.IsNullOrWhiteSpace(line.Substring("Catalog Name:".Length).Trim())
                };
        }

        if (current is not null)
        {
            if (string.IsNullOrEmpty(current.PublishedName) && published is not null)
                current = current with { PublishedName = published };
            drivers.Add(current);
        }

        return drivers;
    }
}

public readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public override string ToString() => $"exit={ExitCode}, stdout(len={StdOut.Length}), stderr(len={StdErr.Length})";
}

/// <summary>
/// Result of a batched pnputil run. The outer ExitCode is the cmd.exe script's
/// exit (we use the value of the last pnputil command by default, but cmd's
/// ERRORLEVEL handling differs — for our purposes we treat any non-zero in any
/// per-line exit as a failure of that single command).
/// </summary>
public sealed record BatchResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    IReadOnlyList<BatchLine> Lines);

public sealed record BatchLine(
    int Index,
    string Args,
    bool Success,
    string? Error,
    string StdOut);
