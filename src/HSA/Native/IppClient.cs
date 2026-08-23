using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HSA.Models;

namespace HSA.Native;

/// <summary>
/// Minimal IPP (Internet Printing Protocol) client used for:
///   - Firmware detection via Get-Printer-Attributes (string attributes)
///   - Consumable query via Get-Printer-Attributes (setOf integer attributes like marker-levels)
///   - v2 firmware updates via PWG 5100.11 IPP System Services
///
/// References:
///   RFC 8010 - IPP/1.1 encoding (delimiter tags, value tags, setOf encoding)
///   RFC 8011 - IPP Model (attribute catalog)
///   PWG 5100.11 - System Services
///   PWG 5100.13 - IPP over USB transport
///   PWG 5100.22 - IPP attribute set for consumables (marker-* attributes)
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
    /// Sends a Get-Printer-Attributes and returns the decoded attribute set with full type info
    /// (so callers can read string and integer setOf attributes like marker-levels).
    /// Returns null on any network / parse failure.
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

            // Read response
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int read;
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
        using var op = new MemoryStream();
        op.WriteByte(0x01); // operation-attributes-tag
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

        var opBytes = op.ToArray();
        var request = new byte[8 + opBytes.Length];
        // IPP 2.0
        request[0] = 0x02; request[1] = 0x00;
        // Get-Printer-Attributes = 0x000B
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
    /// PWG 5100.11 Update-Operation. Sends an IPP Update-Operation (operation-id
    /// 0x0027) that asks the printer to fetch a firmware file from the given URL
    /// and apply it. The protocol is standardized and does NOT bypass any signing
    /// (the printer verifies the firmware signature itself).
    ///
    /// Returns the IPP status code from the printer:
    ///   0x0000 = successful-ok
    ///   0x0500..0x05FF = server-error (e.g. 0x0501 not-authorized, 0x0504 device-error)
    /// Returns -1 if the IPP request itself fails (network, parse, etc.).
    /// </summary>
    public async Task<int> UpdateFirmwareAsync(
        string printerUri, Uri firmwareFileUri, string documentFormat = "application/octet-stream",
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

            var body = BuildUpdateFirmwareRequest(printerUri, firmwareFileUri.AbsoluteUri, documentFormat);
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

            // Read response: header + IPP body
            using var ms = new MemoryStream();
            var headerBytes = new MemoryStream();
            int contentLength = -1;
            var headerDone = false;
            var buf = new byte[4096];
            int read;
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
            if (ms.Length < 4) return -1;
            // IPP response: first two bytes are version (we don't care), next two are status-code.
            var resp = ms.ToArray();
            return (resp[2] << 8) | resp[3];
        }
        catch
        {
            return -1;
        }
    }

    private static byte[] BuildUpdateFirmwareRequest(string printerUri, string firmwareFileUri, string documentFormat)
    {
        using var op = new MemoryStream();
        op.WriteByte(0x01); // operation-attributes-tag
        WriteDelimiter(op, 0x47, "attributes-charset");
        WriteValue(op, 0x47, "utf-8");
        WriteDelimiter(op, 0x48, "attributes-natural-language");
        WriteValue(op, 0x48, "en");
        WriteDelimiter(op, 0x45, "printer-uri");
        WriteValue(op, 0x45, printerUri);
        WriteDelimiter(op, 0x42, "requesting-user-name");
        WriteValue(op, 0x42, Environment.UserName);
        // PWG 5100.11 Update-Operation attributes:
        //  - "document-uri"  (0x45 uri): URL of the firmware file the printer should fetch
        //  - "document-format"  (0x49 mimeMediaType): MIME type of the firmware
        WriteDelimiter(op, 0x45, "document-uri");
        WriteValue(op, 0x45, firmwareFileUri);
        WriteDelimiter(op, 0x49, "document-format");
        WriteValue(op, 0x49, documentFormat);
        op.WriteByte(0x03); // end-of-attributes-tag

        var opBytes = op.ToArray();
        var request = new byte[8 + opBytes.Length];
        // IPP 2.0
        request[0] = 0x02; request[1] = 0x00;
        // Update-Operation = 0x0027 (PWG 5100.11)
        request[2] = 0x00; request[3] = 0x27;
        // request-id 1
        request[4] = 0x00; request[5] = 0x00; request[6] = 0x00; request[7] = 0x01;
        Buffer.BlockCopy(opBytes, 0, request, 8, opBytes.Length);
        return request;
    }

    /// <summary>Quick reachability check for an IPP endpoint.</summary>
    public static async Task<bool> IsReachableAsync(string printerUri, int timeoutMs = 2000, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(printerUri);
            return await IsReachableAsync(IPAddress.Parse(uri.Host),
                uri.IsDefaultPort ? DefaultIppPort : uri.Port, timeoutMs, ct);
        }
        catch { return false; }
    }
}

/// <summary>
/// A single IPP value with its declared type. IPP value tags:
///   0x21 integer, 0x22 boolean, 0x36 name, 0x41 textWithoutLanguage,
///   0x42 nameWithoutLanguage, 0x44 keyword, 0x45 uri, 0x46 uriScheme,
///   0x47 charset, 0x48 naturalLanguage, 0x49 mimeMediaType, 0x36 name.
/// </summary>
public sealed class IppValue
{
    public byte ValueTag { get; }
    public string StringValue { get; }
    public int? IntValue { get; }

    public IppValue(byte tag, string str, int? integer)
    {
        ValueTag = tag;
        StringValue = str;
        IntValue = integer;
    }

    public bool IsInteger => ValueTag == 0x21 || ValueTag == 0x22;
    public bool IsString  => !IsInteger;
}

/// <summary>
/// Typed IPP attribute set. Each attribute can have multiple values (setOf).
/// Use <see cref="GetString"/> for the first string value, <see cref="GetStrings"/>
/// for the full setOf of strings, <see cref="GetInt"/> for the first integer, etc.
/// </summary>
public sealed class IppAttributeSet
{
    private readonly Dictionary<string, List<IppValue>> _attrs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IppValue> Get(string name) =>
        _attrs.TryGetValue(name, out var v) ? v : Array.Empty<IppValue>();

    public string? GetString(string name)
    {
        var v = Get(name);
        var s = v.FirstOrDefault(x => x.IsString);
        return s?.StringValue;
    }

    public IReadOnlyList<string> GetStrings(string name) =>
        Get(name).Where(x => x.IsString).Select(x => x.StringValue).ToList();

    public int? GetInt(string name) => Get(name).FirstOrDefault()?.IntValue;

    public IReadOnlyList<int?> GetInts(string name) =>
        Get(name).Select(x => x.IntValue).ToList();

    public bool Has(string name) => _attrs.ContainsKey(name);

    internal void Add(string name, IppValue value)
    {
        if (!_attrs.TryGetValue(name, out var list))
        {
            list = new List<IppValue>();
            _attrs[name] = list;
        }
        list.Add(value);
    }
}

/// <summary>
/// Decodes an IPP response payload. Per RFC 8010:
///   - Skip the 8-byte IPP header
///   - First byte is the operation-attributes delimiter (0x01) - skip
///   - Walk the payload; for each value-tag:
///       * If it's a delimiter tag (high bit set, 0x01..0x05 range), it's the start of a group
///         and we look up its semantics (printer-attributes 0x02, etc.). For now we just keep parsing.
///       * Otherwise it's a value-tag: read 2-byte name length, name, 2-byte value length, value
///   - For setOf (1setOf X), values have an empty name (length 0) and the value-tag matches
///     the enclosing delimiter. We collect them under the most recent name.
/// </summary>
internal static class IppDecoder
{
    public static IppAttributeSet Decode(byte[] payload)
    {
        var set = new IppAttributeSet();
        if (payload.Length < 9) return set;

        int i = 8; // skip 8-byte IPP header
        // First byte: delimiter-tag (0x01 = operation-attributes). Skip.
        if (i < payload.Length && payload[i] == 0x01) i++;

        string? currentName = null;
        byte? currentDelimiter = null;

        while (i < payload.Length)
        {
            byte tag = payload[i++];

            // Delimiter tags
            if (tag >= 0x01 && tag <= 0x05)
            {
                // 0x01 = operation-attributes
                // 0x02 = job-attributes
                // 0x04 = printer-attributes
                // 0x05 = unsupported-attributes
                // 0x03 = end-of-attributes
                currentDelimiter = tag;
                currentName = null; // no current name in a fresh group
                continue;
            }

            // Value tag
            int nameLen = (payload[i] << 8) | payload[i + 1];
            i += 2;
            if (i + nameLen > payload.Length) break;
            string name = nameLen > 0 ? Encoding.UTF8.GetString(payload, i, nameLen) : string.Empty;
            i += nameLen;

            int valueLen = (payload[i] << 8) | payload[i + 1];
            i += 2;
            if (i + valueLen > payload.Length) break;

            // For setOf, the first value has an empty name but the same value-tag as the delimiter.
            // Subsequent values also have empty names. Once we hit a non-empty name, that's a new
            // attribute. We treat empty-name values as continuations of the most recent named attr.
            if (!string.IsNullOrEmpty(name))
            {
                currentName = name;
            }

            // Parse the value
            string str = Encoding.UTF8.GetString(payload, i, valueLen);
            i += valueLen;
            int? intVal = null;
            if (tag == 0x21 && valueLen == 4)
            {
                intVal = (payload[i - 4] << 24) | (payload[i - 3] << 16) | (payload[i - 2] << 8) | payload[i - 1];
            }
            else if (tag == 0x21 && valueLen == 2)
            {
                intVal = (short)((payload[i - 2] << 8) | payload[i - 1]);
            }
            else if (tag == 0x21 && valueLen == 1)
            {
                intVal = payload[i - 1];
            }
            else if (tag == 0x22 && valueLen == 1)
            {
                intVal = payload[i - 1] == 0 ? 0 : 1;
            }

            if (currentName != null)
            {
                set.Add(currentName, new IppValue(tag, str, intVal));
            }
        }
        return set;
    }
}
