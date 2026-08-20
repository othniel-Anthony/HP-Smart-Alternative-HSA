using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HSA.Models;

namespace HSA.Native;

/// <summary>
/// Minimal IPP (Internet Printing Protocol) client used for firmware detection and (in v2)
/// firmware updates via PWG 5100.11 IPP System Services.
///
/// Reference: RFC 8010 (IPP/1.1 encoding), RFC 8011 (IPP Model), PWG 5100.11 (System Services).
///
/// For v1 we only use the <c>Get-Printer-Attributes</c> operation to read firmware-related
/// attributes. The binary encoder below is intentionally small and only emits the value tags
/// we need. Push-firmware (Update operation) is implemented as a v2 feature — see
/// <see cref="TryUpdateFirmwareAsync"/>.
/// </summary>
public sealed class IppClient
{
    public const int DefaultIppPort = 631;

    private readonly TimeSpan _timeout;

    public IppClient(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public static async Task<bool> IsReachableAsync(IPAddress target, int port = DefaultIppPort, int timeoutMs = 3000, CancellationToken ct = default)
    {
        try
        {
            using var probe = new TcpClient();
            var connectTask = probe.ConnectAsync(target, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, ct));
            return completed == connectTask && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a Get-Printer-Attributes and returns the decoded attribute set.
    /// Returns null on any network / parse failure (treat as "not reachable via IPP").
    /// </summary>
    public async Task<IppAttributeSet?> GetPrinterAttributesAsync(
        string printerUri,
        IReadOnlyCollection<string> requestedAttributes,
        CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(printerUri);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.IsDefaultPort ? DefaultIppPort : uri.Port, ct);
            tcp.SendTimeout = (int)_timeout.TotalMilliseconds;
            tcp.ReceiveTimeout = (int)_timeout.TotalMilliseconds;
            await using var stream = tcp.GetStream();

            // Build Get-Printer-Attributes request
            var body = BuildGetPrinterAttributesRequest(printerUri, requestedAttributes);
            var request =
                $"POST {uri.PathAndQuery} HTTP/1.1\r\n" +
                $"Host: {uri.Host}\r\n" +
                "Content-Type: application/ipp\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Expect:\r\n" +
                "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(headBytes, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);

            // Read response headers + body
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int read;
            // Drain the socket until close or until we have Content-Length worth of body.
            var headerBytes = new MemoryStream();
            int contentLength = -1;
            var headerDone = false;
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                if (!headerDone)
                {
                    headerBytes.Write(buf, 0, read);
                    var headerData = headerBytes.ToArray();
                    var sep = Encoding.ASCII.GetBytes("\r\n\r\n");
                    var idx = IndexOf(headerData, sep);
                    if (idx >= 0)
                    {
                        var headerText = Encoding.ASCII.GetString(headerData, 0, idx);
                        foreach (var line in headerText.Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                _ = int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                        headerDone = true;
                        var leftover = headerData.Length - (idx + 4);
                        if (leftover > 0) ms.Write(headerData, idx + 4, leftover);
                        if (contentLength >= 0 && ms.Length >= contentLength) break;
                    }
                }
                else
                {
                    ms.Write(buf, 0, read);
                    if (contentLength >= 0 && ms.Length >= contentLength) break;
                }
            }
            if (ms.Length == 0) return null;
            return IppDecoder.Decode(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static byte[] BuildGetPrinterAttributesRequest(string printerUri, IReadOnlyCollection<string> requestedAttributes)
    {
        // 1) Build the operation-attributes tag block
        using var op = new MemoryStream();
        // 0x01 = operation-attributes-tag
        op.WriteByte(0x01);
        WriteDelimiter(op, 0x47, "attributes-charset");
        WriteValue(op, 0x47, "utf-8");
        WriteDelimiter(op, 0x48, "attributes-natural-language");
        WriteValue(op, 0x48, "en");
        WriteDelimiter(op, 0x45, "printer-uri");
        WriteValue(op, 0x45, printerUri);
        WriteDelimiter(op, 0x42, "requesting-user-name");
        WriteValue(op, 0x42, Environment.UserName);
        WriteDelimiter(op, 0x44, "requested-attributes");
        foreach (var a in requestedAttributes) WriteValue(op, 0x44, a);
        op.WriteByte(0x03); // end-of-attributes-tag

        // 2) Prepend the 8-byte IPP header
        var opBytes = op.ToArray();
        var request = new byte[8 + opBytes.Length];
        // version 2.0
        request[0] = 0x02; request[1] = 0x00;
        // operation-id 0x000B = Get-Printer-Attributes
        request[2] = 0x00; request[3] = 0x0B;
        // request-id 1
        request[4] = 0x00; request[5] = 0x00; request[6] = 0x00; request[7] = 0x01;
        Buffer.BlockCopy(opBytes, 0, request, 8, opBytes.Length);
        return request;
    }

    private static void WriteDelimiter(MemoryStream s, byte tag, string name)
    {
        s.WriteByte(tag);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        s.WriteByte((byte)(nameBytes.Length & 0xFF));
        s.Write(nameBytes, 0, nameBytes.Length);
    }

    private static void WriteValue(MemoryStream s, byte tag, string value)
    {
        s.WriteByte(tag);
        var len = (ushort)value.Length;
        s.WriteByte((byte)(len >> 8));
        s.WriteByte((byte)(len & 0xFF));
        var bytes = Encoding.UTF8.GetBytes(value);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// v2 placeholder. PWG 5100.11 specifies an Update operation in /system (the printer's
    /// System Services URL) that accepts a firmware file URL. The protocol is standardized
    /// and does NOT bypass any signing — printers that accept unsigned firmware do so, others
    /// reject. Wiring this up is a v2 deliverable.
    /// </summary>
    public Task<bool> TryUpdateFirmwareAsync(string printerUri, Uri firmwareFileUri, CancellationToken ct = default)
    {
        // TODO v2: implement PWG 5100.11 Update operation
        return Task.FromResult(false);
    }
}

/// <summary>
/// Lightweight IPP attribute set. Only supports string-type values, which is sufficient for
/// the firmware attributes we read in v1.
/// </summary>
public sealed class IppAttributeSet
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string name] => _values.TryGetValue(name, out var v) ? v : null;

    internal void Add(string name, string value) => _values[name] = value;
}

internal static class IppDecoder
{
    public static IppAttributeSet Decode(byte[] payload)
    {
        var set = new IppAttributeSet();
        if (payload.Length < 9) return set;
        // Skip 8-byte IPP header, plus the first delimiter-tag (0x01) and the operation attributes.
        // We scan the whole payload looking for attribute name/value pairs; we don't try to be
        // fully spec-correct, only good enough to read a few known string attributes.
        for (int i = 9; i < payload.Length - 4; )
        {
            byte tag = payload[i++];
            if (tag == 0x03 || tag == 0x04 || tag == 0x05) continue; // end-of-*-tag markers
            int nameLen = (payload[i] << 8) | payload[i + 1];
            i += 2;
            if (i + nameLen > payload.Length) break;
            string name = Encoding.UTF8.GetString(payload, i, nameLen);
            i += nameLen;
            int valueLen = (payload[i] << 8) | payload[i + 1];
            i += 2;
            if (i + valueLen > payload.Length) break;
            string value = Encoding.UTF8.GetString(payload, i, valueLen);
            i += valueLen;
            if (name.Length > 0 && value.Length > 0)
                set.Add(name, value);
        }
        return set;
    }
}
