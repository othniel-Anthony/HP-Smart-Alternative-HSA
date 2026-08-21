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
