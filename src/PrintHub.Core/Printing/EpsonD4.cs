using System.Buffers.Binary;
using System.Text;

namespace PrintHub.Core.Printing;

// The packet exchange and byte layouts in this file follow the open-source "epson-usb" library (MIT licence,
// Copyright (c) 2026 Onur Kesim and Ircama), which was verified on real Epson L3250 / L3251 printers over Windows' USB print
// interface. See THIRD-PARTY-NOTICES.md.

/// <summary>EPSON-CTRL messages: two ASCII letters, a 16-bit little-endian length, the payload. These carry EEPROM access and status requests.</summary>
public static class EpsonCtrl
{
    public static byte[] Frame(string name, params byte[] payload)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes(name)) { (byte)payload.Length, (byte)(payload.Length >> 8) };
        b.AddRange(payload);
        return b.ToArray();
    }

    /// <summary>'A' → 41 BE A0, 'B' → 42 BD 21: the opcode letter, its complement, and the letter rotated.</summary>
    static byte[] Opcode(char letter) => new[] { (byte)letter, (byte)~letter, (byte)(((letter >> 1) & 0x7F) | ((letter << 7) & 0x80)) };

    /// <summary>read: 7C 7C 07 00 &lt;key lo&gt; &lt;key hi&gt; 41 BE A0 &lt;addr lo&gt; &lt;addr hi&gt;</summary>
    public static byte[] ReadFrame(byte[] readKey, int address) =>
        Frame("||", readKey.Concat(Opcode('A')).Concat(new[] { (byte)address, (byte)(address >> 8) }).ToArray());

    /// <summary>write: 7C 7C 10 00 &lt;key&gt; 42 BD 21 &lt;addr lo&gt; &lt;addr hi&gt; &lt;value&gt; &lt;8-byte write key&gt;</summary>
    public static byte[] WriteFrame(byte[] readKey, byte[] writeKey, int address, int value) =>
        Frame("||", readKey.Concat(Opcode('B')).Concat(new[] { (byte)address, (byte)(address >> 8), (byte)value }).Concat(writeKey).ToArray());

    public static byte[] DeviceIdFrame() => Frame("di", 0x01);
    public static byte[] StatusFrame() => Frame("st", 0x01);

    /// <summary>"EE:AAAAVV;" → (address, value), or null.</summary>
    public static (int Address, int Value)? ParseRead(byte[]? reply)
    {
        if (reply is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(reply), "EE:([0-9A-Fa-f]{6})");
        if (!m.Success) return null;
        var t = m.Groups[1].Value;
        return (Convert.ToInt32(t[..4], 16), Convert.ToInt32(t[4..6], 16));
    }

    public static bool WriteConfirmed(byte[]? reply) => reply is not null && Encoding.Latin1.GetString(reply).Contains(":OK;", StringComparison.Ordinal);

    /// <summary>The model name from an IEEE 1284 device id reply ("…MDL:L3250 Series;…").</summary>
    public static string? ParseModel(byte[]? reply)
    {
        if (reply is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(reply), "MDL:([^;]+);");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}

/// <summary>IEEE 1284.4 ("D4") packet: a 6-byte header (PSID, SSID, total length big-endian, credit, control) and the payload.</summary>
public readonly record struct D4Packet(byte Psid, byte Ssid, int Length, byte Credit, byte Control, byte[] Payload)
{
    public static byte[] Build(byte psid, byte ssid, byte[] payload, byte credit = 0, byte control = 0)
    {
        var b = new byte[6 + payload.Length];
        b[0] = psid; b[1] = ssid;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)b.Length);
        b[4] = credit; b[5] = control;
        payload.CopyTo(b, 6);
        return b;
    }
}

/// <summary>
/// A conversation with an Epson printer over its USB print interface: enter D4 packet mode, negotiate, open the EPSON-CTRL channel,
/// then exchange EPSON-CTRL messages with credit handling.
/// </summary>
internal sealed class EpsonD4Session : IDisposable
{
    const byte CtrlSocket = 0x02;
    static readonly byte[] EnterSequence = Concat(new byte[] { 0, 0, 0, 0x1B, 0x01 }, Encoding.ASCII.GetBytes("@EJL 1284.4\n@EJL\n@EJL\n"));

    readonly Stream _s;
    byte[] _buf = Array.Empty<byte>();
    byte _rev = 0x20;

    /// <summary>All timeouts are in milliseconds; tests shrink them.</summary>
    public double Scale { get; init; } = 1.0;
    public Action<string>? Trace { get; set; }

    public EpsonD4Session(Stream s) => _s = s;

    int Ms(int ms) => Math.Max(1, (int)(ms * Scale));
    static byte[] Concat(byte[] a, byte[] b) => a.Concat(b).ToArray();

    // ---- transport ----

    async Task<byte[]> ReadChunkAsync(int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Ms(timeoutMs));
        var buf = new byte[1024];
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(deadline - DateTime.UtcNow);
            try
            {
                int n = await _s.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                if (n > 0) return buf.AsSpan(0, n).ToArray();
                await Task.Delay(15, ct).ConfigureAwait(false); // the USB port reports "nothing yet" as an empty read
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        return Array.Empty<byte>();
    }

    async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        Trace?.Invoke("> " + Convert.ToHexString(data));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Ms(3000));
        await _s.WriteAsync(data, cts.Token).ConfigureAwait(false);
        await _s.FlushAsync(cts.Token).ConfigureAwait(false);
    }

    async Task DrainAsync(CancellationToken ct)
    {
        while ((await ReadChunkAsync(300, ct).ConfigureAwait(false)).Length > 0) { }
        _buf = Array.Empty<byte>();
    }

    Task SendAsync(byte psid, byte ssid, byte[] payload, byte credit, CancellationToken ct) =>
        WriteAsync(D4Packet.Build(psid, ssid, payload, credit), ct);

    async Task<D4Packet?> RecvAsync(int timeoutMs, CancellationToken ct)
    {
        while (_buf.Length < 6)
            if (!await FillAsync(timeoutMs, ct).ConfigureAwait(false)) return null;
        while (true)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(_buf.AsSpan(2));
            if (length < 6) { var bad = new D4Packet(_buf[0], _buf[1], length, _buf[4], _buf[5], Array.Empty<byte>()); _buf = _buf[6..]; return bad; }
            if (_buf.Length >= length)
            {
                var p = new D4Packet(_buf[0], _buf[1], length, _buf[4], _buf[5], _buf[6..length]);
                _buf = _buf[length..];
                Trace?.Invoke($"< {p.Psid}/{p.Ssid} {Convert.ToHexString(p.Payload)}");
                return p;
            }
            if (!await FillAsync(timeoutMs, ct).ConfigureAwait(false)) { _buf = _buf[6..]; return null; } // truncated: drop the header so the stream can resync
        }
    }

    async Task<bool> FillAsync(int timeoutMs, CancellationToken ct)
    {
        var chunk = await ReadChunkAsync(timeoutMs, ct).ConfigureAwait(false);
        if (chunk.Length == 0) return false;
        _buf = Concat(_buf, chunk);
        return true;
    }

    // ---- session ----

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DrainAsync(ct).ConfigureAwait(false);
        await WriteAsync(EnterSequence, ct).ConfigureAwait(false);
        await Task.Delay(Ms(200), ct).ConfigureAwait(false);
        await RecvAsync(2500, ct).ConfigureAwait(false); // the printer's answer to leaving packet mode; its content does not matter
        _buf = Array.Empty<byte>();                       // ...and anything else it sent must not be mistaken for the start of the next packet

        byte rev = 0x20; bool ok = false;
        for (int i = 0; i < 3; i++)
        {
            await SendAsync(0, 0, new byte[] { 0x00, rev }, 1, ct).ConfigureAwait(false);
            var reply = await RecvAsync(2500, ct).ConfigureAwait(false);
            var p = reply?.Payload ?? Array.Empty<byte>();
            if (p.Length >= 3 && p[0] == 0x80)
            {
                if (p[1] == 0x00) { ok = true; break; }
                if (p[2] != 0 && p[2] != rev) { rev = p[2]; continue; } // the printer told us which revision it speaks
            }
            break;
        }
        if (!ok) throw new IOException("The printer did not answer the control protocol (D4 Init failed). Switch it off and on, then try again.");
        _rev = rev;

        // OpenChannel on socket 2 ("EPSON-CTRL"); revision 0x10 has an extra initCredit field
        var open = new List<byte> { 0x01, 0x02, CtrlSocket, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00 };
        if (_rev == 0x10) open.AddRange(new byte[] { 0x00, 0x00 });
        await SendAsync(0, 0, open.ToArray(), 1, ct).ConfigureAwait(false);
        var r = await RecvAsync(2500, ct).ConfigureAwait(false);
        if (r is null || r.Value.Payload.Length < 2 || r.Value.Payload[0] != 0x81 || r.Value.Payload[1] != 0x00)
            throw new IOException("The printer would not open its control channel.");
    }

    /// <summary>Send one EPSON-CTRL message and return the printer's answer (null when none arrived).</summary>
    public async Task<byte[]?> RequestAsync(byte[] frame, CancellationToken ct, int tries = 14)
    {
        // take send credit from the printer
        var credit = _rev == 0x10 ? new byte[] { 0x04, 0x02, CtrlSocket, 0x00, 0x80, 0xFF, 0xFF } : new byte[] { 0x04, 0x02, CtrlSocket, 0x00, 0x08 };
        await SendAsync(0, 0, credit, 1, ct).ConfigureAwait(false);
        for (int i = 0; i < 4; i++)
        {
            var reply = await RecvAsync(1500, ct).ConfigureAwait(false);
            if (reply is null || (reply.Value.Payload.Length > 0 && reply.Value.Payload[0] == 0x84)) break;
        }
        // grant the printer credit for its reply, otherwise it stays quiet
        await SendAsync(0, 0, new byte[] { 0x03, 0x02, CtrlSocket, 0x00, 0x08 }, 1, ct).ConfigureAwait(false);
        await RecvAsync(1200, ct).ConfigureAwait(false);

        await SendAsync(CtrlSocket, CtrlSocket, frame, 8, ct).ConfigureAwait(false);
        for (int i = 0; i < tries; i++)
        {
            var p = await RecvAsync(2000, ct).ConfigureAwait(false);
            if (p is { Psid: CtrlSocket } && p.Value.Payload.Length > 0) return p.Value.Payload;
        }
        return null;
    }

    /// <summary>Politely end the D4 session (Exit, command 0x08) so the printer goes back to taking normal print jobs. Never throws.</summary>
    public async Task LeaveAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(Ms(2500));
            await SendAsync(0, 0, new byte[] { 0x08 }, 1, cts.Token).ConfigureAwait(false);
            await RecvAsync(1000, cts.Token).ConfigureAwait(false);
        }
        catch { /* the session is over either way */ }
    }

    public void Dispose() => _s.Dispose();
}
