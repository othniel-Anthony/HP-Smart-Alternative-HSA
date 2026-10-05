using System.Globalization;
using System.Text;

namespace PrintHub.Core.Http;

/// <summary>Reads whole HTTP/1.1 messages from a stream (TCP socket or USB pipe), keeping leftover bytes between messages.</summary>
public sealed class HttpReader
{
    const int UnframedQuietMs = 2_000;
    const int UsbPipeLongWaitMs = Usb.UsbPipeStream.ResponseTimeoutMs;

    readonly Stream _s;
    readonly byte[] _buf = new byte[32768];
    int _pos, _len;

    public HttpReader(Stream s) => _s = s;

    public async Task<HttpMessage?> ReadAsync(bool isResponse, bool requestWasHead = false, CancellationToken ct = default)
    {
        var head = await ReadHeadAsync(ct);
        if (head is null) return null;

        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var msg = new HttpMessage { StartLine = lines[0] };
        foreach (var l in lines.Skip(1))
        {
            int i = l.IndexOf(':');
            if (i > 0) msg.Headers.Add(new(l[..i].Trim(), l[(i + 1)..].Trim()));
        }

        int status = msg.StatusCode;
        bool noBody = isResponse && (requestWasHead || status is >= 100 and < 200 or 204 or 304);
        if (noBody) return msg;

        if ((msg.GetHeader("Transfer-Encoding") ?? "").Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            msg.Body = await ReadChunkedAsync(ct);
        }
        else if (long.TryParse(msg.GetHeader("Content-Length"), out var n))
        {
            msg.Body = await ReadExactAsync((int)n, ct);
        }
        else if (isResponse)
        {
            // No framing: body runs until the device goes quiet / closes. The head has arrived, so only wait a moment for the rest.
            var ms = new MemoryStream();
            (_s as IReadTimeoutStream)?.SetReadTimeout(UnframedQuietMs);
            try { while (true) { if (_pos == _len && !await FillAsync(ct)) break; ms.Write(_buf, _pos, _len - _pos); _pos = _len; } }
            catch (TimeoutException) { }
            finally { (_s as IReadTimeoutStream)?.SetReadTimeout(UsbPipeLongWaitMs); }
            msg.Body = ms.ToArray();
        }
        return msg;
    }

    async Task<bool> FillAsync(CancellationToken ct)
    {
        int n = await _s.ReadAsync(_buf.AsMemory(), ct);
        if (n <= 0) return false;
        _pos = 0; _len = n;
        return true;
    }

    async Task<string?> ReadHeadAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos == _len && !await FillAsync(ct))
                return sb.Length == 0 ? null : throw new IOException("Connection closed mid-header");
            while (_pos < _len)
            {
                sb.Append((char)_buf[_pos++]);
                if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r')
                    return sb.ToString(0, sb.Length - 4);
                if (sb.Length > 65536) throw new IOException("HTTP header too large");
            }
        }
    }

    async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        int got = 0;
        while (got < count)
        {
            if (_pos == _len && !await FillAsync(ct)) throw new IOException("Connection closed mid-body");
            int take = Math.Min(count - got, _len - _pos);
            Buffer.BlockCopy(_buf, _pos, result, got, take);
            _pos += take; got += take;
        }
        return result;
    }

    async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos == _len && !await FillAsync(ct)) throw new IOException("Connection closed mid-chunk");
            while (_pos < _len)
            {
                char c = (char)_buf[_pos++];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
        }
    }

    async Task<byte[]> ReadChunkedAsync(CancellationToken ct)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var sizeLine = (await ReadLineAsync(ct)).Split(';')[0].Trim();
            int size = int.Parse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (size == 0) break;
            ms.Write(await ReadExactAsync(size, ct));
            await ReadLineAsync(ct); // CRLF after chunk
        }
        while ((await ReadLineAsync(ct)).Length > 0) { } // trailers
        return ms.ToArray();
    }
}
