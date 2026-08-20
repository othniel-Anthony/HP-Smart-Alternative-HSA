using System.Net;
using System.Net.Sockets;
using HSA.Models;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace HSA.Native;

/// <summary>
/// Minimal SNMP v1/v2c client used to read printer identity, status, and (on HP) firmware
/// version. Uses RFC 3805 (Printer MIB) plus a handful of HP-specific enterprise OIDs.
/// </summary>
public sealed class SnmpClient
{
    public const string DefaultCommunity = "public";
    public const int DefaultTimeoutMs = 2000;
    public const int DefaultPort = 161;

    // RFC 3805 Printer MIB
    public static readonly ObjectIdentifier PrtGeneralPrinterName = new("1.3.6.1.2.1.43.5.1.1.16.1");
    public static readonly ObjectIdentifier PrtGeneralSerialNumber = new("1.3.6.1.2.1.43.5.1.1.17.1");
    public static readonly ObjectIdentifier HrDeviceDescr = new("1.3.6.1.2.1.25.3.2.1.3.1");
    public static readonly ObjectIdentifier SysDescr = new("1.3.6.1.2.1.1.1.0");

    // HP-specific firmware version (LASERJET / DesignJet family; returns an OctetString)
    public static readonly ObjectIdentifier HpLaserJetFirmware = new("1.3.6.1.4.1.11.2.4.3.1.1");

    // RFC 3805 prtMarkerSuppliesTable (1.3.6.1.2.1.43.11.1.1) — used for ink/toner levels
    public static readonly ObjectIdentifier MarkerSuppliesDescription = new("1.3.6.1.2.1.43.11.1.1.6");
    public static readonly ObjectIdentifier MarkerSuppliesClass       = new("1.3.6.1.2.1.43.11.1.1.4");
    public static readonly ObjectIdentifier MarkerSuppliesColorIndex  = new("1.3.6.1.2.1.43.11.1.1.3");
    public static readonly ObjectIdentifier MarkerSuppliesMaxCapacity = new("1.3.6.1.2.1.43.11.1.1.8");
    public static readonly ObjectIdentifier MarkerSuppliesLevel        = new("1.3.6.1.2.1.43.11.1.1.9");

    // RFC 3805 prtMarkerColorantTable (1.3.6.1.2.1.43.12.1.1) — color names
    public static readonly ObjectIdentifier MarkerColorantValue = new("1.3.6.1.2.1.43.12.1.1.4");

    private readonly string _community;
    private readonly int _timeoutMs;

    public SnmpClient(string community = DefaultCommunity, int timeoutMs = DefaultTimeoutMs)
    {
        _community = community;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Reads a single OID and returns its value as a string (best-effort).
    /// </summary>
    public async Task<string?> GetAsync(IPAddress target, ObjectIdentifier oid, CancellationToken ct = default)
    {
        var results = await GetManyAsync(target, new[] { oid }, ct);
        return results.TryGetValue(oid, out var v) ? v : null;
    }

    /// <summary>
    /// Reads several OIDs in one round-trip. Returns a dictionary keyed by OID; missing
    /// keys indicate the printer did not return a value (noSuchObject, noSuchInstance, or
    /// transport error).
    /// </summary>
    public async Task<IReadOnlyDictionary<ObjectIdentifier, string>> GetManyAsync(
        IPAddress target, IEnumerable<ObjectIdentifier> oids, CancellationToken ct = default)
    {
        var result = new Dictionary<ObjectIdentifier, string>();
        var oidList = oids.ToList();
        if (oidList.Count == 0) return result;

        var endpoint = new IPEndPoint(target, DefaultPort);
        var variables = oidList.Select(o => new Variable(o)).ToList();
        try
        {
            var reply = await Messenger.GetAsync(VersionCode.V2, endpoint,
                new OctetString(_community), variables, ct);
            foreach (var v in reply)
            {
                if (v?.Data is null) continue;
                var s = v.Data.ToString().Trim();
                if (string.IsNullOrEmpty(s)) continue;
                result[v.Id] = s;
            }
        }
        catch
        {
            // best-effort: return what we have
        }
        return result;
    }

    public static bool IsSnmpReachable(IPAddress target, int timeoutMs = DefaultTimeoutMs, int port = DefaultPort)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SendTimeout = timeoutMs;
            probe.ReceiveTimeout = timeoutMs;
            var connectTask = probe.ConnectAsync(target, port);
            return connectTask.Wait(timeoutMs) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the subtree rooted at each <paramref name="columns"/> OID and returns every
    /// variable found, grouped by the trailing row index. The dictionary key is the index
    /// (last component of the full OID), the value is the list of (column, value) pairs for
    /// that row.
    /// </summary>
    /// <remarks>
    /// Useful for tables like prtMarkerSuppliesTable where the column is the OID prefix
    /// and the row index is the trailing component. We don't try to reconstruct a typed
    /// model here — that's the caller's job.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(ObjectIdentifier Column, string Value)>>>
        WalkTableAsync(IPAddress target, ObjectIdentifier tableRoot, CancellationToken ct = default)
    {
        var result = new Dictionary<int, IReadOnlyList<(ObjectIdentifier, string)>>();
        var endpoint = new IPEndPoint(target, DefaultPort);
        try
        {
            // The synchronous Walk API in Lextm.SharpSnmpLib accumulates into a caller-supplied
            // list. We pass a list and capture the result. Wrapped in Task.Run so the caller can await.
            var accumulator = new List<Variable>();
            await Task.Run(() => Messenger.Walk(
                VersionCode.V2,
                endpoint,
                new OctetString(_community),
                tableRoot,
                accumulator,
                _timeoutMs,
                WalkMode.WithinSubtree), ct);

            var byRow = new Dictionary<int, List<(ObjectIdentifier, string)>>();
            foreach (var v in accumulator)
            {
                if (v?.Data is null) continue;
                var s = v.Data.ToString().Trim();
                if (string.IsNullOrEmpty(s)) continue;
                var parts = v.Id.ToString().Split('.');
                if (parts.Length == 0) continue;
                if (!int.TryParse(parts[^1], out var rowIndex)) continue;
                if (!byRow.TryGetValue(rowIndex, out var list))
                {
                    list = new List<(ObjectIdentifier, string)>();
                    byRow[rowIndex] = list;
                }
                list.Add((v.Id, s));
            }
            foreach (var kv in byRow)
                result[kv.Key] = kv.Value;
        }
        catch
        {
            // best-effort
        }
        return result;
    }
}
