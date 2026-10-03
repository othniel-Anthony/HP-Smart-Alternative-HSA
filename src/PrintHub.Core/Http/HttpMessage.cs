using System.Text;

namespace PrintHub.Core.Http;

/// <summary>A fully buffered HTTP/1.1 request or response.</summary>
public sealed class HttpMessage
{
    public string StartLine { get; set; } = "";
    public List<KeyValuePair<string, string>> Headers { get; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string? GetHeader(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public void RemoveHeader(string name) =>
        Headers.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void SetHeader(string name, string value)
    {
        RemoveHeader(name);
        Headers.Add(new(name, value));
    }

    public int StatusCode =>
        StartLine.StartsWith("HTTP/", StringComparison.Ordinal) && StartLine.Split(' ', 3) is { Length: >= 2 } p && int.TryParse(p[1], out var c) ? c : 0;

    public string Method => StartLine.Split(' ', 2)[0];

    /// <summary>Serialise with a Content-Length framing (never chunked), which every embedded stack handles.</summary>
    public byte[] Serialize(bool alwaysContentLength = true)
    {
        RemoveHeader("Transfer-Encoding");
        if (alwaysContentLength || Body.Length > 0) SetHeader("Content-Length", Body.Length.ToString());
        var sb = new StringBuilder(StartLine).Append("\r\n");
        foreach (var h in Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var all = new byte[head.Length + Body.Length];
        head.CopyTo(all, 0);
        Body.CopyTo(all, head.Length);
        return all;
    }
}
