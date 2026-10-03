using System.Buffers.Binary;
using System.Text;

namespace PrintHub.Core.Ipp;

public static class IppTag
{
    public const byte OperationAttributes = 0x01, JobAttributes = 0x02, EndOfAttributes = 0x03,
        PrinterAttributes = 0x04, UnsupportedAttributes = 0x05;
    public const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23, OctetString = 0x30, DateTime = 0x31,
        Resolution = 0x32, RangeOfInteger = 0x33, BegCollection = 0x34, TextWithLanguage = 0x35,
        NameWithLanguage = 0x36, EndCollection = 0x37, Text = 0x41, Name = 0x42, Keyword = 0x44, Uri = 0x45,
        UriScheme = 0x46, Charset = 0x47, NaturalLanguage = 0x48, MimeMediaType = 0x49, MemberAttrName = 0x4A;
}

public static class IppOp
{
    public const ushort PrintJob = 0x0002, ValidateJob = 0x0004, CancelJob = 0x0008, GetJobAttributes = 0x0009,
        GetJobs = 0x000A, GetPrinterAttributes = 0x000B, IdentifyPrinter = 0x003C;
}

public readonly record struct IppResolution(int X, int Y, int Units);
public readonly record struct IppRange(int Lower, int Upper);

public sealed class IppAttribute
{
    public byte Group { get; init; }
    public byte Tag { get; init; }
    public string Name { get; init; } = "";
    public List<object> Values { get; } = new();
}

/// <summary>Minimal IPP/1.1+2.0 message encoder/decoder (RFC 8010).</summary>
public sealed class IppMessage
{
    public byte VersionMajor { get; set; } = 2;
    public byte VersionMinor { get; set; }
    /// <summary>Operation code on a request; status code on a response.</summary>
    public ushort Code { get; set; }
    public int RequestId { get; set; } = 1;
    public List<IppAttribute> Attributes { get; } = new();

    public bool Successful => Code < 0x0100;

    public IppMessage Add(byte group, byte tag, string name, params object[] values)
    {
        var a = new IppAttribute { Group = group, Tag = tag, Name = name };
        a.Values.AddRange(values);
        Attributes.Add(a);
        return this;
    }

    public IppAttribute? Get(string name, byte? group = null) =>
        Attributes.FirstOrDefault(a => a.Name == name && (group is null || a.Group == group));

    public string? GetString(string name) => Get(name)?.Values.FirstOrDefault()?.ToString();
    public int? GetInt(string name) => Get(name)?.Values.OfType<int>().Cast<int?>().FirstOrDefault();
    public bool? GetBool(string name) => Get(name)?.Values.OfType<bool>().Cast<bool?>().FirstOrDefault();
    public List<string> GetStrings(string name) => Get(name)?.Values.Select(v => v.ToString() ?? "").ToList() ?? new();

    public static IppMessage CreateRequest(ushort op, Uri printerUri, string? user = "HSA", int requestId = 1)
    {
        var m = new IppMessage { Code = op, RequestId = requestId };
        m.Add(IppTag.OperationAttributes, IppTag.Charset, "attributes-charset", "utf-8");
        m.Add(IppTag.OperationAttributes, IppTag.NaturalLanguage, "attributes-natural-language", "en");
        m.Add(IppTag.OperationAttributes, IppTag.Uri, "printer-uri", ToIppUri(printerUri));
        if (user is not null) m.Add(IppTag.OperationAttributes, IppTag.Name, "requesting-user-name", user);
        return m;
    }

    public static string ToIppUri(Uri u)
    {
        var scheme = u.Scheme is "https" or "ipps" ? "ipps" : "ipp";
        return $"{scheme}://{u.Host}:{u.Port}{u.AbsolutePath}";
    }

    public byte[] Encode()
    {
        var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        ms.WriteByte(VersionMajor); ms.WriteByte(VersionMinor);
        U16(Code); I32(RequestId);

        byte currentGroup = 0;
        foreach (var a in Attributes)
        {
            if (a.Group != currentGroup) { ms.WriteByte(a.Group); currentGroup = a.Group; }
            for (int i = 0; i < a.Values.Count; i++)
            {
                ms.WriteByte(a.Tag);
                var name = i == 0 ? Encoding.UTF8.GetBytes(a.Name) : Array.Empty<byte>();
                U16(name.Length); ms.Write(name);
                var val = EncodeValue(a.Values[i]);
                U16(val.Length); ms.Write(val);
            }
        }
        ms.WriteByte(IppTag.EndOfAttributes);
        return ms.ToArray();
    }

    static byte[] EncodeValue(object v)
    {
        switch (v)
        {
            case string s: return Encoding.UTF8.GetBytes(s);
            case int i: { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, i); return b; }
            case bool bo: return new[] { (byte)(bo ? 1 : 0) };
            case IppResolution r:
            {
                var b = new byte[9];
                BinaryPrimitives.WriteInt32BigEndian(b, r.X); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), r.Y); b[8] = (byte)r.Units;
                return b;
            }
            case IppRange rg:
            {
                var b = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(b, rg.Lower); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), rg.Upper);
                return b;
            }
            case byte[] raw: return raw;
            default: throw new ArgumentException($"Unsupported IPP value type {v.GetType()}");
        }
    }

    /// <summary>Decode a message; <paramref name="bodyOffset"/> is where any trailing document data starts.</summary>
    public static IppMessage Decode(byte[] d, out int bodyOffset)
    {
        if (d.Length < 9) throw new InvalidDataException("IPP message too short");
        var m = new IppMessage { VersionMajor = d[0], VersionMinor = d[1], Code = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2)), RequestId = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)) };
        int p = 8;
        byte group = 0;
        IppAttribute? last = null;

        while (p < d.Length)
        {
            byte tag = d[p++];
            if (tag == IppTag.EndOfAttributes) break;
            if (tag <= 0x05) { group = tag; last = null; continue; }

            int nameLen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)); p += 2;
            string name = Encoding.UTF8.GetString(d, p, nameLen); p += nameLen;
            int valLen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)); p += 2;

            if (tag == IppTag.BegCollection)
            {
                p += valLen;
                p = SkipCollection(d, p);
                var colAttr = nameLen > 0 ? new IppAttribute { Group = group, Tag = tag, Name = name } : last;
                if (nameLen > 0) { m.Attributes.Add(colAttr!); last = colAttr; }
                colAttr?.Values.Add("{collection}");
                continue;
            }

            object value = DecodeValue(tag, d.AsSpan(p, valLen));
            p += valLen;

            if (nameLen == 0 && last is not null) last.Values.Add(value);
            else
            {
                last = new IppAttribute { Group = group, Tag = tag, Name = name };
                last.Values.Add(value);
                m.Attributes.Add(last);
            }
        }
        bodyOffset = p;
        return m;
    }

    static int SkipCollection(byte[] d, int p)
    {
        int depth = 1;
        while (depth > 0 && p < d.Length)
        {
            byte tag = d[p++];
            int nameLen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)); p += 2 + nameLen;
            int valLen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)); p += 2 + valLen;
            if (tag == IppTag.BegCollection) depth++;
            else if (tag == IppTag.EndCollection) depth--;
        }
        return p;
    }

    static object DecodeValue(byte tag, ReadOnlySpan<byte> v) => tag switch
    {
        IppTag.Integer or IppTag.Enum when v.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(v),
        IppTag.Boolean when v.Length == 1 => v[0] != 0,
        IppTag.Resolution when v.Length == 9 => new IppResolution(BinaryPrimitives.ReadInt32BigEndian(v), BinaryPrimitives.ReadInt32BigEndian(v[4..]), v[8]),
        IppTag.RangeOfInteger when v.Length == 8 => new IppRange(BinaryPrimitives.ReadInt32BigEndian(v), BinaryPrimitives.ReadInt32BigEndian(v[4..])),
        IppTag.OctetString or IppTag.DateTime => v.ToArray(),
        IppTag.TextWithLanguage or IppTag.NameWithLanguage => DecodeLangString(v),
        _ => Encoding.UTF8.GetString(v),
    };

    static string DecodeLangString(ReadOnlySpan<byte> v)
    {
        if (v.Length < 4) return "";
        int langLen = BinaryPrimitives.ReadUInt16BigEndian(v);
        if (v.Length < 4 + langLen) return "";
        int textLen = BinaryPrimitives.ReadUInt16BigEndian(v[(2 + langLen)..]);
        return Encoding.UTF8.GetString(v.Slice(4 + langLen, Math.Min(textLen, v.Length - 4 - langLen)));
    }
}
