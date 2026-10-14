using System.Text;
using System.Text.Json;

namespace PrintHub.Core.Printing;

/// <summary>What an HP printer says about its two wireless sides: Wi-Fi Direct (the printer's own access point) and Wi-Fi (joined to a network).</summary>
public sealed record HpWirelessState(
    bool DirectOn, string DirectSsid, bool DirectInSetupMode, string ConnectionMethod, string Version,
    string? DirectAddress, string StationState, string? StationSsid, string? StationAddress, string? Band)
{
    /// <summary>The Wi-Fi Direct passcode the printer reports (manual connection method), or null. Sent back when switching on so it is not lost.</summary>
    public string? Passphrase { get; init; }
    /// <summary>The editable part of the network name as the printer reports it, or null (then taken from the name after its 13-character prefix).</summary>
    public string? SsidSuffix { get; init; }

    /// <summary>"Connected to MyNetwork", "Not set up", ...</summary>
    public string StationText => StationState.ToLowerInvariant() switch
    {
        "connected" => StationSsid is { Length: > 0 } ? $"Connected to “{StationSsid}”" + (StationAddress is { Length: > 0 } ? $" ({StationAddress})" : "") : "Connected",
        "notconfigured" => "Not set up (no network joined)",
        "disconnected" => "Not connected",
        "connecting" => "Connecting…",
        "" => "Unknown",
        _ => StationState,
    };
}

/// <summary>
/// Wi-Fi Direct on HP printers that serve the JSON web services ("CDM", for example the Smart Tank 5xx/6xx). The paths and bodies are the ones the printer's own
/// web page uses (its WifiDirect page script): the state is read from /cdm/ioConfig/v2/wifiDirectConfig and the adapters, and Wi-Fi Direct is switched by
/// PUTting that document back with "enabled" changed. Switching it off sends only <c>enabled</c> and <c>version</c>.
/// </summary>
public static class HpWireless
{
    public const string DirectPath = "cdm/ioConfig/v2/wifiDirectConfig";
    public const string AdaptersPath = "cdm/ioConfig/v2/adapterConfigs";

    // the printer's own page: SSID = 13-character prefix ("DIRECT-xx-HP ") + a suffix the owner may edit; "HP-Setup" SSIDs (shown with filler characters) are the not-yet-set-up form
    const int SsidPrefixLength = 13;
    const string AutomaticPassphrase = "12345678";   // what the printer's page sends for the "automatic" connection method

    public static HpWirelessState? Parse(string directJson, string? adaptersJson)
    {
        try
        {
            using var d = JsonDocument.Parse(directJson);
            var root = d.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("enabled", out _)) return null;
            string ssid = Str(root, "ssid") ?? "";
            string method = Str(root, "connectionMethod") ?? "";
            string station = ""; string? band = null, stSsid = null, stIp = null, apIp = null;
            if (adaptersJson is not null)
            {
                try
                {
                    using var a = JsonDocument.Parse(adaptersJson);
                    if (a.RootElement.TryGetProperty("wifi0", out var w0))
                    {
                        station = Str(w0, "connectionState") ?? "";
                        if (w0.TryGetProperty("wifiStationConfig", out var sc))
                        {
                            band = Str(sc, "band");
                            if (sc.TryGetProperty("wirelessNetwork", out var wn)) stSsid = Str(wn, "ssid");
                        }
                        stIp = Ipv4(w0);
                    }
                    if (a.RootElement.TryGetProperty("wifi1", out var w1)) apIp = Ipv4(w1);
                }
                catch (JsonException) { }
            }
            return new HpWirelessState(
                string.Equals(Str(root, "enabled"), "true", StringComparison.OrdinalIgnoreCase), ssid,
                ssid.Contains("HP=Setup", StringComparison.Ordinal), method, Str(root, "version") ?? "2.1.0",
                apIp, station, stSsid, stIp, band) { Passphrase = Str(root, "passphrase"), SsidSuffix = Str(root, "ssidSuffix") };
        }
        catch (JsonException) { return null; }
    }

    static string? Ipv4(JsonElement adapter) =>
        adapter.TryGetProperty("ipv4", out var v4) && v4.TryGetProperty("address", out var a) && Str(a, "ip") is { Length: > 0 } ip && ip != "0.0.0.0" ? ip : null;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => v.ToString(), _ => null } : null;

    /// <summary>The request body to switch Wi-Fi Direct on or off, built from what the printer currently reports.</summary>
    public static string BuildBody(HpWirelessState current, bool on)
    {
        if (!on) return JsonSerializer.Serialize(new { version = current.Version, enabled = "false" });
        var body = new Dictionary<string, string> { ["version"] = current.Version, ["enabled"] = "true" };
        string method = current.ConnectionMethod is { Length: > 0 } ? current.ConnectionMethod : "automatic";
        body["connectionMethod"] = method;
        if (!current.DirectInSetupMode)
        {
            if (current.SsidSuffix is { Length: > 0 } suffix) body["ssidSuffix"] = suffix;
            else if (current.DirectSsid.Length > SsidPrefixLength) body["ssidSuffix"] = current.DirectSsid[SsidPrefixLength..];
        }
        if (method.Equals("automatic", StringComparison.OrdinalIgnoreCase)) body["passphrase"] = AutomaticPassphrase;
        else if (current.Passphrase is { Length: > 0 } pass) body["passphrase"] = pass;      // manual: keep the one the printer has
        return JsonSerializer.Serialize(body);
    }

    /// <summary>The Wi-Fi state, or null when this printer does not serve Wi-Fi Direct settings (older models, or no web services).</summary>
    public static async Task<HpWirelessState?> ReadAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        string? direct = await GetAsync(http, new Uri(baseUri, DirectPath), ct).ConfigureAwait(false);
        if (direct is null) return null;
        string? adapters = await GetAsync(http, new Uri(baseUri, AdaptersPath), ct).ConfigureAwait(false);
        return Parse(direct, adapters);
    }

    static async Task<string?> GetAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            using var r = await http.GetAsync(uri, ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; return null; }
    }

    /// <summary>
    /// Switches Wi-Fi Direct on or off and waits until the printer reports the new state (the printer's own page allows it 90 s). Returns a sentence for the user.
    /// Throws <see cref="InvalidOperationException"/> when the printer refuses, <see cref="TimeoutException"/> when it never reports the change.
    /// </summary>
    public static async Task<string> SetDirectAsync(HttpClient http, Uri baseUri, bool on, IProgress<string>? progress, CancellationToken ct,
        TimeSpan? maxWait = null, TimeSpan? poll = null)
    {
        var current = await ReadAsync(http, baseUri, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This printer does not offer Wi-Fi Direct settings over its web services.");
        if (current.DirectOn == on) return on ? "Wi-Fi Direct is already on." : "Wi-Fi Direct is already off.";

        var body = BuildBody(current, on);
        using (var req = new HttpRequestMessage(HttpMethod.Put, new Uri(baseUri, DirectPath)) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
            progress?.Report(on ? "Turning Wi-Fi Direct on…" : "Turning Wi-Fi Direct off…");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            Diag.Log($"HP CDM: Wi-Fi Direct {(on ? "on" : "off")} -> HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
            {
                var text = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                if (text.Length > 200) text = text[..200];
                throw new InvalidOperationException($"The printer refused to turn Wi-Fi Direct {(on ? "on" : "off")} (HTTP {(int)resp.StatusCode}" + (text.Length > 0 ? ": " + text : "") + "). It may be busy, or Wi-Fi may be switched off on the printer.");
            }
        }

        var every = poll ?? TimeSpan.FromSeconds(2);
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < (maxWait ?? TimeSpan.FromSeconds(90)))
        {
            await Task.Delay(every, ct).ConfigureAwait(false);
            var now = await ReadAsync(http, baseUri, ct).ConfigureAwait(false);
            if (now is null) continue;      // the radio restarts: the printer may not answer for a moment
            if (now.DirectOn == on)
                return on ? $"Wi-Fi Direct is on. Network name: {now.DirectSsid}." : "Wi-Fi Direct is off.";
        }
        throw new TimeoutException($"The printer accepted the request but did not report Wi-Fi Direct as {(on ? "on" : "off")}. Check the printer's Wi-Fi light or its network report.");
    }
}
