using System.Text;
using System.Text.Json;

namespace PrintHub.Core.Printing;

/// <summary>How to turn the EEPROM cells of one wear counter into a number (informational: shown as a percentage, never used to decide a write).</summary>
public sealed class EpsonCounter
{
    public string Description { get; init; } = "";
    public int Max { get; init; }
    /// <summary>Cells holding the value, low byte first.</summary>
    public List<int> Cells { get; init; } = new();
    /// <summary>Extra cells that contribute a masked part of the value, each multiplied by a weight.</summary>
    public List<(int Address, int Mask, int Weight)> Extras { get; init; } = new();
}

/// <summary>A set of EEPROM cells that make up the waste-ink pad state, with the value each one is reset to.</summary>
public sealed class EpsonPadGroup
{
    public string Kind { get; init; } = "";
    public string Description { get; init; } = "";
    public List<int> Addresses { get; init; } = new();
    public List<int> ResetValues { get; init; } = new();
    public List<EpsonCounter> Counters { get; init; } = new();
}

/// <summary>After the reset: clear bits in a cell (cell = cell AND mask).</summary>
public readonly record struct EpsonCloseStep(int Address, int AndMask);

public sealed class EpsonModelSpec
{
    public string Key { get; init; } = "";
    public int ReadKey { get; init; }
    /// <summary>The 8 bytes that go on the wire with every write.</summary>
    public byte[] WriteKey { get; init; } = Array.Empty<byte>();
    public int ReadLen { get; init; }
    public int WriteLen { get; init; }
    public int MemHigh { get; init; }
    public List<EpsonPadGroup> PadGroups { get; init; } = new();
    public List<EpsonCloseStep> Close { get; init; } = new();

    /// <summary>The two bytes of the read key as they go on the wire (low byte first).</summary>
    public byte[] ReadKeyBytes => new[] { (byte)(ReadKey & 0xFF), (byte)((ReadKey >> 8) & 0xFF) };

    public IEnumerable<int> AllAddresses => PadGroups.SelectMany(g => g.Addresses).Concat(Close.Select(c => c.Address)).Distinct();

    /// <summary>Why this entry cannot be used, or null when it can.</summary>
    public string? Problem
    {
        get
        {
            if (PadGroups.Count == 0 || PadGroups.All(g => g.Addresses.Count == 0)) return "The database lists no waste counters for this model.";
            if (ReadLen != 2 || WriteLen != 2) return "This is an older model that uses a different memory-access format, which HSA does not support.";
            if (WriteKey.Length != 8) return "The database entry has no usable write key.";
            if (AllAddresses.Any(a => a < 0 || a > Math.Min(MemHigh, 0xFFFF))) return "The database entry points outside the printer's memory.";
            if (PadGroups.Any(g => g.Addresses.Count != g.ResetValues.Count)) return "The database entry is inconsistent.";
            return null;
        }
    }

    /// <summary>Two entries that would do exactly the same thing to a printer.</summary>
    public string Fingerprint =>
        $"{ReadKey}|{Convert.ToHexString(WriteKey)}|{MemHigh}|" + string.Join(";", PadGroups.Select(g => string.Join(",", g.Addresses) + "=" + string.Join(",", g.ResetValues)))
        + "|" + string.Join(",", Close.Select(c => $"{c.Address}&{c.AndMask}"));
}

public sealed record EpsonMatch(EpsonModelSpec? Spec, string? ModelKey, string? Problem);

/// <summary>
/// The per-model data the counter reset needs: memory addresses, access keys and the values to restore. HSA does not ship this
/// file; the user provides it (Maintenance page → "Choose database file"). Format: <c>{"schema_version":4,"specs":{…},"models":{…}}</c>.
/// </summary>
public sealed class EpsonDatabase
{
    public const int SchemaVersion = 4;

    readonly Dictionary<string, EpsonModelSpec> _models = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _index = new();   // canonical alias → model keys
    readonly Dictionary<string, string> _byKey = new();            // canonical model key → model key (an exact key always wins over an alias)

    public int ModelCount => _models.Count;
    public string SourcePath { get; private set; } = "";

    public static EpsonDatabase Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..]; // a UTF-8 byte-order mark is common in files saved by Windows tools
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || !root.TryGetProperty("specs", out var specs))
            throw new InvalidDataException("This is not an Epson model database (it has no \"models\" and \"specs\" sections).");
        if (root.TryGetProperty("schema_version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != SchemaVersion)
            throw new InvalidDataException($"This database uses format version {v.GetInt32()}; HSA reads version {SchemaVersion}.");

        var db = new EpsonDatabase { SourcePath = path };
        var specCache = new Dictionary<string, EpsonModelSpec>();
        foreach (var m in models.EnumerateObject())
        {
            EpsonModelSpec? spec = null;
            try
            {
                if (m.Value.TryGetProperty("spec", out var sref) && sref.ValueKind == JsonValueKind.String)
                {
                    var name = sref.GetString()!;
                    if (!specCache.TryGetValue(name, out spec))
                    {
                        if (!specs.TryGetProperty(name, out var se)) continue;
                        specCache[name] = spec = ReadSpec(m.Name, se);
                    }
                }
                else spec = ReadSpec(m.Name, m.Value);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException) { continue; } // skip a broken entry, keep the rest

            db._models[m.Name] = spec;
            db._byKey[Canon(m.Name)] = m.Name;
            if (m.Value.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                foreach (var a in aliases.EnumerateArray()) if (a.ValueKind == JsonValueKind.String) db.IndexAlias(m.Name, a.GetString()!);
        }
        if (db._models.Count == 0) throw new InvalidDataException("The database contains no usable models.");
        return db;
    }

    static EpsonModelSpec ReadSpec(string key, JsonElement e)
    {
        var groups = new List<EpsonPadGroup>();
        if (e.TryGetProperty("pad_groups", out var pg))
            foreach (var g in pg.EnumerateArray())
            {
                var counters = new List<EpsonCounter>();
                if (g.TryGetProperty("counters", out var cs))
                    foreach (var c in cs.EnumerateArray())
                    {
                        var cells = new List<int>(); var extras = new List<(int, int, int)>();
                        foreach (var b in c.GetProperty("bytes").EnumerateArray())
                        {
                            if (b.ValueKind == JsonValueKind.Number) cells.Add(b.GetInt32());
                            else extras.Add((b.GetProperty("addr").GetInt32(), b.GetProperty("mask").GetInt32(), b.GetProperty("weight").GetInt32()));
                        }
                        counters.Add(new EpsonCounter
                        {
                            Description = c.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "",
                            Max = c.TryGetProperty("max", out var mx) ? mx.GetInt32() : 0,
                            Cells = cells, Extras = extras,
                        });
                    }
                groups.Add(new EpsonPadGroup
                {
                    Kind = g.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    Description = g.TryGetProperty("desc", out var gd) ? gd.GetString() ?? "" : "",
                    Addresses = g.GetProperty("addresses").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                    ResetValues = g.GetProperty("reset").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                    Counters = counters,
                });
            }

        var close = new List<EpsonCloseStep>();
        if (e.TryGetProperty("close", out var cl))
            foreach (var c in cl.EnumerateArray()) close.Add(new EpsonCloseStep(c.GetProperty("addr").GetInt32(), c.GetProperty("and").GetInt32()));

        var wkey = e.GetProperty("wkey").GetString() ?? "";
        return new EpsonModelSpec
        {
            Key = key,
            ReadKey = e.GetProperty("rkey").GetInt32(),
            WriteKey = wkey.Select(ch => (byte)ch).ToArray(), // the key is stored as characters 0-255, one per byte
            ReadLen = e.GetProperty("rlen").GetInt32(),
            WriteLen = e.GetProperty("wlen").GetInt32(),
            MemHigh = e.GetProperty("mem_high").GetInt32(),
            PadGroups = groups,
            Close = close,
        };
    }

    // ---- finding a model from the name Windows or the printer gives ----

    static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    { "epson", "series", "stylus", "photo", "printer", "usb", "net", "network", "ecotank", "workforce", "expression", "home", "premium", "small", "office", "pro", "all", "in", "one", "inkjet", "color", "colour", "multifunction" };

    static string Canon(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    void Index(string canonicalSource, string modelKey)
    {
        var c = Canon(canonicalSource);
        if (c.Length < 2) return;
        if (!_index.TryGetValue(c, out var set)) _index[c] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(modelKey);
    }

    void IndexAlias(string modelKey, string alias)
    {
        foreach (var piece in alias.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var words = piece.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var core = words.Where(w => !Noise.Contains(w)).ToArray();
            if (core.Length > 0) { Index(string.Concat(core), modelKey); Index(core[^1], modelKey); }
        }
    }

    /// <summary>Look a printer up by any of the names it is known by (Windows queue name, USB description, IEEE 1284 model).</summary>
    public EpsonMatch Find(params string?[] names)
    {
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var words = name!.Split(new[] { ' ', '(', ')', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.Contains(w)).ToArray();
            if (words.Length == 0) continue;

            // the most specific guess first: all the words together, then single words, then neighbouring pairs
            var tiers = new List<string> { Canon(string.Concat(words)) };
            tiers.AddRange(words.Select(Canon));
            for (int i = 0; i + 1 < words.Length; i++) tiers.Add(Canon(words[i] + words[i + 1]));

            foreach (var t in tiers.Distinct())
            {
                if (_byKey.TryGetValue(t, out var exact)) return new EpsonMatch(_models[exact], exact, _models[exact].Problem);
                if (!_index.TryGetValue(t, out var keys)) continue;
                var specs = keys.Select(k => _models[k]).GroupBy(s => s.Fingerprint).ToList();
                if (specs.Count == 1) return new EpsonMatch(specs[0].First(), keys.OrderBy(k => k.Length).First(), specs[0].First().Problem);
                return new EpsonMatch(null, null, $"“{name}” matches several different models in the database ({string.Join(", ", keys.OrderBy(k => k).Take(5))}). HSA will not guess.");
            }
        }
        return new EpsonMatch(null, null, "This printer's model is not in the database.");
    }
}
