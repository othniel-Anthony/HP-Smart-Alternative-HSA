using HSA.Models;
using HSA.Native;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// Batches pnputil operations across one or more drivers into a single elevated
/// process so the user only sees ONE UAC prompt. The default per-driver path
/// (PnpUtil.RemoveDriverAsync) prompts UAC per call - removing 8 HP drivers is 8
/// prompts, which is unusable. This manager collects all the operations, runs
/// them as a single elevated cmd.exe script, and reports per-line results.
/// </summary>
public sealed class DriverStoreManager
{
    private readonly ILogger<DriverStoreManager> _log;

    public DriverStoreManager(ILogger<DriverStoreManager> log) => _log = log;

    /// <summary>
    /// Removes a list of drivers in one elevated batch. Each driver's
    /// <see cref="PnpDeviceRemoval"/> runs first (per PnP device instance), then
    /// the package's <c>/delete-driver</c> runs. All inside a single cmd.exe
    /// process so the user sees one UAC prompt.
    /// </summary>
    /// <returns>
    /// Per-driver outcome; also surfaces which sub-operations succeeded inside
    /// the returned <see cref="RegistryCleanupResult"/>s.
    /// </returns>
    public async Task<IReadOnlyList<RegistryCleanupResult>> RemoveBatchWithRegistryCleanupAsync(
        IReadOnlyList<(DriverInfo Driver, IReadOnlyList<string> InstanceIds)> plan,
        CancellationToken ct = default)
    {
        if (plan is null || plan.Count == 0)
            return Array.Empty<RegistryCleanupResult>();

        // Build the argument list. Order matters: remove-device first (per instance)
        // for each driver, then delete-driver (per driver).
        var args = new List<string>(plan.Sum(p => p.InstanceIds.Count) + plan.Count);
        var driverLines = new List<(DriverInfo Driver, IReadOnlyList<int> LineIndexes, int DeleteIndex)>(plan.Count);

        foreach (var (driver, ids) in plan)
        {
            var lineIdx = new List<int>(ids.Count);
            foreach (var id in ids)
            {
                // Escape any backslashes/quotes in the instance id so it survives
                // round-tripping through the .bat file. pnputil's /remove-device
                // expects a literal string argument.
                var safe = id.Replace("\"", "\\\"");
                lineIdx.Add(args.Count);
                args.Add($"/remove-device \"{safe}\" /force");
            }
            // For /delete-driver, the published name is like "oem42.inf" - no spaces,
            // no quotes needed, but be safe.
            var pn = driver.PublishedName?.Replace("\"", "\\\"") ?? string.Empty;
            var deleteIdx = args.Count;
            args.Add($"/delete-driver \"{pn}\" /force");
            driverLines.Add((driver, lineIdx, deleteIdx));
        }

        _log.LogInformation("Running batched pnputil for {Count} drivers ({Total} commands, single UAC)",
            plan.Count, args.Count);

        var result = await PnpUtil.RunBatchAsync(args, ct);

        // Map per-line results back to per-driver outcomes.
        var outcomes = new List<RegistryCleanupResult>(plan.Count);
        for (int d = 0; d < driverLines.Count; d++)
        {
            var (driver, removeIdx, deleteIdx) = driverLines[d];
            var deviceRemovals = new List<PnpDeviceRemoval>(removeIdx.Count);
            foreach (var idx in removeIdx)
            {
                // result.Lines[idx] is a nullable BatchLine record (default = null when
                // we got fewer lines than we expected - e.g. cmd.exe died early).
                var line = idx < result.Lines.Count ? result.Lines[idx] : null;
                if (line is null)
                {
                    deviceRemovals.Add(new PnpDeviceRemoval("(unknown)", false, -1, "no result from batched pnputil"));
                    continue;
                }
                deviceRemovals.Add(new PnpDeviceRemoval(
                    InstanceId: line.Args.Contains('"') ? ExtractInstanceId(line.Args) : "(unknown)",
                    Success: line.Success,
                    ExitCode: line.Success ? 0 : 1,
                    Error: line.Success ? null : line.Error));
            }
            var deleteLine = deleteIdx < result.Lines.Count ? result.Lines[deleteIdx] : null;
            outcomes.Add(new RegistryCleanupResult
            {
                DriverPublishedName = driver.PublishedName ?? string.Empty,
                DriverOriginalName   = driver.OriginalName,
                DeviceRemovals       = deviceRemovals,
                DriverPackageRemoved = deleteLine?.Success ?? false,
                DriverPackageError   = (deleteLine is null)
                    ? "no result from batched pnputil"
                    : (deleteLine.Success ? null : deleteLine.Error)
            });
        }
        return outcomes;
    }

    private static string ExtractInstanceId(string args)
    {
        // args looks like: /remove-device "instance-id" /force
        var start = args.IndexOf('"');
        var end = args.IndexOf('"', start + 1);
        if (start < 0 || end <= start) return "(unknown)";
        return args.Substring(start + 1, end - start - 1);
    }
}
