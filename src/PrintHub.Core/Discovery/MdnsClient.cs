using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PrintHub.Core.Discovery;

public sealed record MdnsService(string Instance, string Type, string Host, int Port,
    IReadOnlyDictionary<string, string> Txt, IReadOnlyList<IPAddress> Addresses);

/// <summary>Small multicast-DNS (Bonjour) browser for printer service types. IPv4 only.</summary>
public static class MdnsClient
{
    public static readonly string[] PrinterServiceTypes =
        { "_ipp._tcp", "_ipps._tcp", "_uscan._tcp", "_uscans._tcp", "_printer._tcp", "_pdl-datastream._tcp" };

    static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<List<MdnsService>> BrowseAsync(IEnumerable<string>? types = null, TimeSpan? duration = null, CancellationToken ct = default)
    {
        types ??= PrinterServiceTypes;
        var total = duration ?? TimeSpan.FromSeconds(3);
        var db = new Db();
        var sockets = OpenSockets();
        try
        {
            var typeList = types.ToList();
            foreach (var s in sockets) Send(s, typeList.Select(t => (t + ".local", (ushort)12)));
            await ReceiveFor(sockets, TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.5), db, ct);

            // second round: resolve instances / hosts we only half know
            var follow = new List<(string, ushort)>();
            foreach (var inst in db.Instances.Keys.ToList())
            {
                if (!db.Srv.ContainsKey(inst)) follow.Add((inst, 33));
                if (!db.Txt.ContainsKey(inst)) follow.Add((inst, 16));
            }
            foreach (var srv in db.Srv.Values.ToList())
                if (!db.Addr.ContainsKey(srv.Host)) follow.Add((srv.Host, 1));
            if (follow.Count > 0)
            {
                foreach (var s in sockets) Send(s, follow);
                await ReceiveFor(sockets, TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.5), db, ct);
            }
            else await ReceiveFor(sockets, TimeSpan.FromMilliseconds(total.TotalMilliseconds * 0.5), db, ct);
        }
        finally { foreach (var s in sockets) s.Dispose(); }

        return db.Build();
    }

    static List<Socket> OpenSockets()
    {
        var list = new List<Socket>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.Bind(new IPEndPoint(ua.Address, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ua.Address.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    list.Add(s);
                }
                catch (SocketException) { }
            }
        }
        return list;
    }

    static void Send(Socket s, IEnumerable<(string name, ushort type)> questions)
    {
        var qs = questions.ToList();
        for (int i = 0; i < qs.Count; i += 8) // keep each packet small
        {
            var chunk = qs.Skip(i).Take(8).ToList();
            var ms = new MemoryStream();
            void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            U16(0); U16(0); U16(chunk.Count); U16(0); U16(0); U16(0);
            foreach (var (name, type) in chunk)
            {
                foreach (var label in name.Split('.')) { var b = Encoding.UTF8.GetBytes(label); ms.WriteByte((byte)b.Length); ms.Write(b); }
                ms.WriteByte(0);
                U16(type); U16(0x8001); // QU bit: ask for a unicast reply
            }
            try { s.SendTo(ms.ToArray(), MdnsEndpoint); } catch (SocketException) { }
        }
    }

    static async Task ReceiveFor(List<Socket> sockets, TimeSpan span, Db db, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(span);
        await Task.WhenAll(sockets.Select(async s =>
        {
            var buf = new byte[9000];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, cts.Token);
                    lock (db) { try { db.Parse(buf, r.ReceivedBytes); } catch { /* malformed packet */ } }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }));
    }

    internal sealed class Db
    {
        public readonly Dictionary<string, string> Instances = new();               // full instance name -> service type
        public readonly Dictionary<string, (string Host, int Port)> Srv = new();
        public readonly Dictionary<string, Dictionary<string, string>> Txt = new();
        public readonly Dictionary<string, HashSet<IPAddress>> Addr = new(StringComparer.OrdinalIgnoreCase);

        public void Parse(byte[] b, int len)
        {
            if (len < 12) return;
            int p = 4;
            int qd = U16(b, ref p), an = U16(b, ref p), ns = U16(b, ref p), ar = U16(b, ref p);
            for (int i = 0; i < qd; i++) { ReadName(b, ref p); p += 4; }
            for (int i = 0; i < an + ns + ar; i++)
            {
                var name = ReadName(b, ref p);
                int type = U16(b, ref p); p += 2; p += 4;
                int rdLen = U16(b, ref p);
                int end = p + rdLen;
                switch (type)
                {
                    case 12: // PTR
                    {
                        int q = p; var target = ReadName(b, ref q);
                        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && name.StartsWith("_"))
                            Instances[target] = name[..^6];
                        break;
                    }
                    case 33: // SRV
                    {
                        // priority(2) weight(2) port(2) target
                        int port = (b[p + 4] << 8) | b[p + 5];
                        int q = p + 6;
                        var host = ReadName(b, ref q);
                        Srv[name] = (host, port);
                        break;
                    }
                    case 16: // TXT
                    {
                        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        int q = p;
                        while (q < end)
                        {
                            int l = b[q++]; if (l == 0) continue;
                            var kv = Encoding.UTF8.GetString(b, q, Math.Min(l, end - q)); q += l;
                            int eq = kv.IndexOf('=');
                            if (eq > 0) d[kv[..eq]] = kv[(eq + 1)..]; else d[kv] = "";
                        }
                        Txt[name] = d;
                        break;
                    }
                    case 1 when rdLen == 4: // A
                    {
                        if (!Addr.TryGetValue(name, out var set)) Addr[name] = set = new();
                        set.Add(new IPAddress(b.AsSpan(p, 4)));
                        break;
                    }
                }
                p = end;
            }
        }

        public List<MdnsService> Build()
        {
            var res = new List<MdnsService>();
            foreach (var (full, type) in Instances)
            {
                if (!Srv.TryGetValue(full, out var srv)) continue;
                var inst = full.EndsWith("." + type + ".local", StringComparison.OrdinalIgnoreCase) ? full[..^(type.Length + 7)] : full;
                Addr.TryGetValue(srv.Host, out var addrs);
                res.Add(new MdnsService(inst, type, srv.Host, srv.Port,
                    Txt.GetValueOrDefault(full) ?? new Dictionary<string, string>(),
                    addrs?.ToList() ?? new List<IPAddress>()));
            }
            return res;
        }

        static int U16(byte[] b, ref int p) { int v = (b[p] << 8) | b[p + 1]; p += 2; return v; }

        static string ReadName(byte[] b, ref int p)
        {
            var sb = new StringBuilder();
            int pos = p; bool jumped = false; int guard = 0;
            while (true)
            {
                int len = b[pos];
                if (len == 0) { pos++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    int ptr = ((len & 0x3F) << 8) | b[pos + 1];
                    if (!jumped) { p = pos + 2; jumped = true; }
                    pos = ptr;
                    if (++guard > 32) throw new InvalidDataException("DNS compression loop");
                    continue;
                }
                pos++;
                sb.Append(Encoding.UTF8.GetString(b, pos, len)).Append('.');
                pos += len;
            }
            if (!jumped) p = pos;
            return sb.ToString().TrimEnd('.');
        }
    }
}
