using System.Xml.Linq;

namespace PrintHub.Core.Ipp;

/// <summary>
/// HP's "web services" (LEDM) interface: plain HTTP + XML under /DevMgmt, served over the network and, on many entry-level
/// inkjets that have no IPP at all, over the printer's USB web-services interface. Used to read ink levels when IPP cannot.
/// </summary>
public static class LedmClient
{
    public const string ConsumablesPath = "DevMgmt/ConsumableConfigDyn.xml";

    public static async Task<List<SupplyLevel>?> GetSuppliesAsync(Uri baseUri, HttpClient http, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync(new Uri(baseUri, ConsumablesPath), cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var xml = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var list = ParseConsumables(xml);
        return list.Count > 0 ? list : null;
    }

    // Supply types that are shown as a level. Print heads, drums and kits are not.
    static readonly string[] InkLike = { "ink", "inkCartridge", "toner", "tonerCartridge", "rechargeableToner", "inkTank" };

    /// <summary>
    /// Turns the ConsumableConfigDyn document into supply rows, the way HP's own software reads it: cartridges and ink tanks only, nothing for a
    /// missing one, and "unknown" (shown as a dash) when the printer gives no usable percentage.
    /// </summary>
    public static List<SupplyLevel> ParseConsumables(string xml)
    {
        var result = new List<SupplyLevel>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return result; }

        foreach (var info in doc.Descendants().Where(e => e.Name.LocalName == "ConsumableInfo"))
        {
            string? Get(string local) => info.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim();

            var type = Get("ConsumableTypeEnum") ?? "";
            if (!InkLike.Contains(type, StringComparer.OrdinalIgnoreCase)) continue;

            var state = Get("ConsumableState") ?? "";
            if (state.Equals("missing", StringComparison.OrdinalIgnoreCase)) continue;

            int percent = int.TryParse(Get("ConsumablePercentageLevelRemaining"), out var p) && p >= 0 ? Math.Clamp(p, 0, 100) : -1;
            if (percent == 0 && string.Equals(Get("MeasuredQuantityState"), "unknown", StringComparison.OrdinalIgnoreCase)) percent = -1; // 0 with no measurement means "not known"

            var (name, color) = Describe(Get("ConsumableLabelCode") ?? "", Get("ConsumableStation"));
            result.Add(new SupplyLevel(name, type.Contains("toner", StringComparison.OrdinalIgnoreCase) ? "toner" : "ink", color, percent, 10, 100));
        }
        return result;
    }

    internal static (string Name, string Color) Describe(string code, string? station) => code.ToUpperInvariant() switch
    {
        "K" => ("Black", "#303030"),
        "C" => ("Cyan", "#00B7EB"),
        "M" => ("Magenta", "#EC008C"),
        "Y" => ("Yellow", "#FFD400"),
        "CMY" => ("Tri-colour", "#00B7EB,#EC008C,#FFD400"),
        "CCMMY" or "CCMM" => ("Photo colour", "#00B7EB,#EC008C,#FFD400"),
        "LC" => ("Light cyan", "#7FD6F5"),
        "LM" => ("Light magenta", "#F58FC8"),
        "PK" => ("Photo black", "#303030"),
        "GY" or "G" => ("Grey", "#808080"),
        "" => (station is { Length: > 0 } ? $"Cartridge {station}" : "Cartridge", "#808080"),
        _ => (code, "#808080"),
    };
}
