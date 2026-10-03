using System.Net;
using System.Net.Sockets;

namespace PrintHub.Core.Http;

/// <summary>
/// Printers present self-signed certificates on https/ipps. We accept those, but only for loopback and private-network
/// addresses, so a printer name or address can never be used to quietly weaken TLS to an internet host.
/// </summary>
public static class LocalTls
{
    public static bool IsPrivateHost(string host)
    {
        if (!IPAddress.TryParse(host, out var ip))
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.');
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
    }

    public static HttpClientHandler CreateHandler() => new()
    {
        ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None || (req.RequestUri is { } u && IsPrivateHost(u.Host)),
        AutomaticDecompression = DecompressionMethods.All,
    };
}
