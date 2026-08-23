using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// One driver update returned by the Windows Update Agent (WUA) search.
/// </summary>
public sealed record DriverUpdate(
    string Title,
    string DriverClass,
    string DriverManufacturer,
    string DriverModel,
    string DriverProvider,
    string DriverVersion,
    string UpdateId,
    string? DownloadUrl)
{
    public string DisplayLabel => $"{Title} — {DriverManufacturer} {DriverModel} ({DriverVersion})";
}

/// <summary>
/// Windows Update Agent (WUA) driver search. Uses late binding to the
/// Microsoft.Update.Session COM object so we don't have to add a COM reference
/// to the project. Only "Driver"-class updates are returned - we never want
/// app/OS updates in this UI.
/// </summary>
public sealed class WindowsUpdateClient
{
    private readonly ILogger<WindowsUpdateClient> _log;

    public WindowsUpdateClient(ILogger<WindowsUpdateClient> log) => _log = log;

    public async Task<IReadOnlyList<DriverUpdate>> SearchDriversAsync(
        string? hardwareId = null,
        string? modelKeyword = null,
        CancellationToken ct = default)
    {
        try
        {
            // Build the WUA search criteria. Type='Driver' limits to drivers.
            // IsInstalled=0 means "not yet on this machine".
            var criteriaParts = new List<string> { "Type='Driver'", "IsInstalled=0" };
            if (!string.IsNullOrWhiteSpace(hardwareId))
            {
                // WUA uses single quotes around values that contain spaces. The
                // HardwareIDs criterion matches any of the provided IDs.
                criteriaParts.Add($"HardwareIDs='{EscapeWql(hardwareId)}'");
            }
            var criteria = string.Join(" AND ", criteriaParts);

            return await Task.Run(() =>
            {
                var results = new List<DriverUpdate>();
                dynamic? session = null;
                dynamic? searcher = null;
                dynamic? searchResult = null;
                try
                {
                    var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                        ?? throw new InvalidOperationException("Microsoft.Update.Session not registered (WUA unavailable).");
                    session = Activator.CreateInstance(sessionType);
                    searcher = session!.CreateUpdateSearcher();
                    searchResult = searcher!.Search(criteria);
                    int count = (int)searchResult.Updates.Count;
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        dynamic update = searchResult.Updates.Item(i);
                        try
                        {
                            var title = (string)update.Title;
                            var driverClass = TryGet(() => (string)update.DriverClass);
                            var driverMfr   = TryGet(() => (string)update.DriverManufacturer);
                            var driverModel = TryGet(() => (string)update.DriverModel);
                            var driverProv  = TryGet(() => (string)update.DriverProvider);
                            var driverVer   = TryGet(() => (string)update.DriverVersion);
                            var updateId    = TryGet(() => (string)update.Identity.UpdateID);
                            // If the user typed a model keyword, filter loosely against
                            // title/model strings - WUA's criteria language doesn't support
                            // a per-token text search.
                            if (!string.IsNullOrWhiteSpace(modelKeyword))
                            {
                                var kw = modelKeyword.Trim();
                                var hay = (title + " " + driverModel + " " + driverMfr).ToLowerInvariant();
                                if (!hay.Contains(kw.ToLowerInvariant())) continue;
                            }
                            results.Add(new DriverUpdate(
                                Title: title,
                                DriverClass: driverClass,
                                DriverManufacturer: driverMfr,
                                DriverModel: driverModel,
                                DriverProvider: driverProv,
                                DriverVersion: driverVer,
                                UpdateId: updateId,
                                DownloadUrl: null));
                        }
                        finally
                        {
                            // WUA dynamic objects don't need explicit release, but be tidy
                        }
                    }
                }
                finally
                {
                    if (searchResult is not null) MarshalRelease(searchResult);
                    if (searcher is not null) MarshalRelease(searcher);
                    if (session is not null) MarshalRelease(session);
                }
                return results;
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WUA search failed");
            return Array.Empty<DriverUpdate>();
        }
    }

    private static string TryGet(Func<string> getter)
    {
        try { return getter() ?? string.Empty; } catch { return string.Empty; }
    }

    private static void MarshalRelease(object obj)
    {
        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(obj); } catch { }
    }

    /// <summary>
    /// Escape single quotes for WQL. WQL uses doubled single quotes ('') to
    /// represent a literal apostrophe inside a string literal.
    /// </summary>
    private static string EscapeWql(string s) => s.Replace("'", "''");
}
