using HSA.Models;
using HSA.Native;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

public interface IDriverService
{
    /// <summary>Enumerates ALL driver store packages and joins with installed printers.</summary>
    Task<IReadOnlyList<DriverInfo>> GetAllAsync(bool hpOnly, CancellationToken ct = default);

    /// <summary>Enumerates only driver packages that are referenced by installed printers.</summary>
    Task<IReadOnlyList<DriverInfo>> GetInUseAsync(bool hpOnly, CancellationToken ct = default);

    /// <summary>Returns the PnP device instance IDs bound to a driver's INF (for preview/inspection).</summary>
    Task<IReadOnlyList<string>> GetPnpInstanceIdsAsync(DriverInfo driver, CancellationToken ct = default);

    /// <summary>Removes a single driver package. Requires admin elevation.</summary>
    Task<CommandResult> RemoveAsync(DriverInfo driver, bool force, CancellationToken ct = default);

    /// <summary>Removes every HP driver package in the driver store.</summary>
    Task<IReadOnlyList<(DriverInfo Driver, CommandResult Result)>> RemoveAllHpAsync(
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a single driver package AND cleans up its registry footprint: first
    /// /remove-device for each PnP instance (clears Services/Enum/Spooler entries),
    /// then /delete-driver to remove the package from the store.
    /// </summary>
    Task<RegistryCleanupResult> RemoveWithRegistryCleanupAsync(DriverInfo driver, CancellationToken ct = default);

    /// <summary>Bulk variant of RemoveWithRegistryCleanupAsync.</summary>
    Task<IReadOnlyList<RegistryCleanupResult>> RemoveAllHpWithRegistryCleanupAsync(
        IProgress<(int Done, int Total, string Current, string Phase)>? progress = null,
        CancellationToken ct = default);

    /// <summary>Adds an INF to the driver store and triggers a rescan.</summary>
    Task<CommandResult> InstallFromInfAsync(string infPath, CancellationToken ct = default);
}

/// <summary>
/// Driver store + spooler-driver service. Uses pnputil for store operations and joins results
/// with WMI's Win32_PrinterDriver to identify which driver packages are actively used.
/// </summary>
public sealed class DriverService : IDriverService
{
    private readonly ILogger<DriverService> _log;

    public DriverService(ILogger<DriverService> log) => _log = log;

    public async Task<IReadOnlyList<DriverInfo>> GetAllAsync(bool hpOnly, CancellationToken ct = default)
    {
        var all = await PnpUtil.EnumerateDriversAsync(ct);
        var used = await GetUsedByMapAsync(ct);
        var joined = all.Select(d => d with
        {
            UsedByPrinters = used.TryGetValue(d.OriginalName, out var ps)
                ? ps : Array.Empty<string>()
        }).ToList();
        return hpOnly ? joined.Where(d => d.IsHp).ToList() : joined;
    }

    public async Task<IReadOnlyList<DriverInfo>> GetInUseAsync(bool hpOnly, CancellationToken ct = default)
    {
        var all = await GetAllAsync(false, ct);
        var filtered = all.Where(d => d.UsedByPrinters.Count > 0).ToList();
        return hpOnly ? filtered.Where(d => d.IsHp).ToList() : filtered;
    }

    public async Task<CommandResult> RemoveAsync(DriverInfo driver, bool force, CancellationToken ct = default)
    {
        if (driver is null) throw new ArgumentNullException(nameof(driver));
        if (string.IsNullOrWhiteSpace(driver.PublishedName))
            throw new InvalidOperationException("Driver has no published name; cannot remove.");

        // Safety: if a printer still references this driver, refuse unless force=true.
        if (!force && driver.UsedByPrinters.Count > 0)
        {
            throw new InvalidOperationException(
                $"Driver '{driver.OriginalName}' is used by {driver.UsedByPrinters.Count} printer(s): " +
                $"{string.Join(", ", driver.UsedByPrinters)}. " +
                $"Remove the printer(s) first, or pass force=true.");
        }

        _log.LogInformation("Removing driver {Name} (force={Force})", driver.PublishedName, force);
        var result = await PnpUtil.RemoveDriverAsync(driver.PublishedName, force, ct);
        if (!result.Success)
        {
            _log.LogError("Failed to remove driver {Name}: exit={Exit} stderr={Err}",
                driver.PublishedName, result.ExitCode, result.StdErr);
        }
        return result;
    }

    public async Task<IReadOnlyList<(DriverInfo Driver, CommandResult Result)>> RemoveAllHpAsync(
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        var hp = (await GetAllAsync(true, ct)).ToList();
        var results = new List<(DriverInfo, CommandResult)>(hp.Count);

        _log.LogInformation("Starting bulk removal of {Count} HP driver packages", hp.Count);

        for (int i = 0; i < hp.Count; i++)
        {
            var d = hp[i];
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, hp.Count, d.OriginalName));

            // Always force: the user explicitly asked to clean ALL HP drivers.
            var res = await RemoveAsync(d, force: true, ct);
            results.Add((d, res));
        }
        progress?.Report((hp.Count, hp.Count, string.Empty));
        return results;
    }

    public Task<IReadOnlyList<string>> GetPnpInstanceIdsAsync(DriverInfo driver, CancellationToken ct = default)
    {
        if (driver is null) throw new ArgumentNullException(nameof(driver));
        return Task.Run(
            () => (IReadOnlyList<string>)WmiHelper.QueryPnpInstanceIdsForInf(driver.InfPath ?? string.Empty),
            ct);
    }
    public async Task<RegistryCleanupResult> RemoveWithRegistryCleanupAsync(
        DriverInfo driver, CancellationToken ct = default)
    {
        if (driver is null) throw new ArgumentNullException(nameof(driver));
        if (string.IsNullOrWhiteSpace(driver.PublishedName))
            throw new InvalidOperationException("Driver has no published name; cannot remove.");

        _log.LogInformation("Registry-cleanup remove for {Name} (inf={Inf})",
            driver.PublishedName, driver.InfPath);

        // Step 1: look up PnP device instances bound to this INF.
        var instanceIds = await Task.Run(
            () => WmiHelper.QueryPnpInstanceIdsForInf(driver.InfPath ?? string.Empty), ct);

        // Step 2: for every PnP instance, /remove-device. This is the only pnputil
        // sub-command that clears HKLM\...\Services\<svc> + Enum\<inst> for the device.
        var removals = new List<PnpDeviceRemoval>(instanceIds.Count);
        foreach (var id in instanceIds)
        {
            ct.ThrowIfCancellationRequested();
            _log.LogInformation("Removing PnP device {Id} (for driver {Driver})", id, driver.PublishedName);
            var res = await PnpUtil.RemoveDeviceAsync(id, force: true, ct);
            removals.Add(new PnpDeviceRemoval(
                id,
                res.Success,
                res.ExitCode,
                string.IsNullOrWhiteSpace(res.StdErr) ? null : res.StdErr.Trim()));
        }

        // Step 3: delete the driver package from the store. Attempt even if some
        // device removals failed - it frees the disk space and removes the oem*.inf.
        var pkgRes = await PnpUtil.RemoveDriverAsync(driver.PublishedName, force: true, ct);

        return new RegistryCleanupResult
        {
            DriverPublishedName = driver.PublishedName,
            DriverOriginalName   = driver.OriginalName,
            DeviceRemovals       = removals,
            DriverPackageRemoved = pkgRes.Success,
            DriverPackageError   = pkgRes.Success ? null : pkgRes.StdErr.Trim()
        };
    }

    public async Task<IReadOnlyList<RegistryCleanupResult>> RemoveAllHpWithRegistryCleanupAsync(
        IProgress<(int Done, int Total, string Current, string Phase)>? progress = null,
        CancellationToken ct = default)
    {
        var hp = (await GetAllAsync(true, ct)).ToList();
        var results = new List<RegistryCleanupResult>(hp.Count);

        _log.LogInformation("Starting bulk registry-cleanup removal of {Count} HP driver packages", hp.Count);

        for (int i = 0; i < hp.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, hp.Count, hp[i].OriginalName, "Removing PnP devices"));
            var r = await RemoveWithRegistryCleanupAsync(hp[i], ct);
            results.Add(r);
        }
        progress?.Report((hp.Count, hp.Count, string.Empty, "Done"));
        return results;
    }
    public async Task<CommandResult> InstallFromInfAsync(string infPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(infPath))
            throw new ArgumentException("INF path is required.", nameof(infPath));
        _log.LogInformation("Adding driver INF {Path}", infPath);
        var result = await PnpUtil.AddDriverAsync(infPath, ct);
        if (result.Success)
        {
            _log.LogInformation("Driver INF added; triggering rescan");
            await PnpUtil.RescanAsync(ct);
        }
        else
        {
            _log.LogError("Failed to add driver INF: exit={Exit} stderr={Err}", result.ExitCode, result.StdErr);
        }
        return result;
    }

    private static async Task<Dictionary<string, string[]>> GetUsedByMapAsync(CancellationToken ct)
    {
        var drivers = await Task.Run(() => WmiHelper.QueryPrinterDrivers(), ct);
        var printers = await Task.Run(() => WmiHelper.QueryPrinters(), ct);
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var printer in printers)
        {
            if (string.IsNullOrEmpty(printer.DriverName)) continue;
            // printer.DriverName is the spooler-driver name; match against driver rows whose Name matches.
            var spooler = drivers.FirstOrDefault(d =>
                string.Equals(d.Name, printer.DriverName, StringComparison.OrdinalIgnoreCase));
            if (spooler is null) continue;
            var infName = spooler.InfName;
            if (string.IsNullOrEmpty(infName)) continue;
            if (!map.TryGetValue(infName, out var list))
            {
                list = Array.Empty<string>();
                map[infName] = list;
            }
            map[infName] = list.Append(printer.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return map;
    }
}


/// <summary>
/// Per-driver result of a registry-cleanup removal. The driver is considered fully
/// cleaned when <see cref="DriverPackageRemoved"/> is true (the package is gone from
/// the store). <see cref="DeviceRemovals"/> lists every PnP device we attempted to
/// unregister along with the pnputil exit code for each.
/// </summary>
public sealed class RegistryCleanupResult
{
    public required string DriverPublishedName { get; init; }
    public required string DriverOriginalName { get; init; }
    public required IReadOnlyList<PnpDeviceRemoval> DeviceRemovals { get; init; }
    public required bool DriverPackageRemoved { get; init; }
    public string? DriverPackageError { get; init; }

    public bool FullySucceeded =>
        DriverPackageRemoved && DeviceRemovals.All(d => d.Success);

    public string Summary
    {
        get
        {
            var total = DeviceRemovals.Count;
            var ok = DeviceRemovals.Count(d => d.Success);
            return $"devices {ok}/{total} removed" +
                   (DriverPackageRemoved ? ", driver package removed" : ", driver package FAILED");
        }
    }
}

public sealed record PnpDeviceRemoval(string InstanceId, bool Success, int ExitCode, string? Error);