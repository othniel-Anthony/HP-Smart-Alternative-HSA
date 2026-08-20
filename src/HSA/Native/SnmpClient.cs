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
}
