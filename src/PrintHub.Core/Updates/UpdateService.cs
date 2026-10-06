using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrintHub.Core.Updates;

/// <summary>A newer release found on GitHub, with the exact files needed to install it.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri ExeUrl, long ExeSize, string ExeName, Uri SumsUrl, Uri PageUrl);

/// <summary>
/// Looks for a newer HSA release on GitHub and downloads it safely. Nothing is installed here: the caller decides when to restart into the update.
/// The download is refused unless it comes from this project's GitHub releases, has the size the release lists, matches the SHA-256 in the release's
/// SHA256SUMS.txt, and is a Windows program whose version is the one announced. (The checksum file is published next to the exe, so this protects
/// against damaged or substituted downloads, not against someone who controls the GitHub release itself: HSA is not code-signed yet.)
/// </summary>
public static class UpdateService
{
    public const string Repo = "othniel-Anthony/HP-Smart-Alternative-HSA";
    /// <summary>Test hook: a URL that answers like GitHub's "latest release" API, served from this machine.</summary>
    public const string OverrideEnvVar = "HSA_UPDATE_URL";

    public static Uri LatestReleaseApi =>
        Environment.GetEnvironmentVariable(OverrideEnvVar) is { Length: > 0 } o && Uri.TryCreate(o, UriKind.Absolute, out var u)
            ? u : new Uri($"https://api.github.com/repos/{Repo}/releases/latest");

    static bool IsOverridden => Environment.GetEnvironmentVariable(OverrideEnvVar) is { Length: > 0 };

    public static HttpClient CreateClient(string currentVersion)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HSA", currentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    // ---------------------------------------------------------------- versions

    /// <summary>"v0.15.1" or "0.15.1" (up to four numbers). Anything with a suffix such as "-beta" is a pre-release and is not offered.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text.Trim(), @"^[vV]?(\d+(?:\.\d+){1,3})$");
        if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var v)) return false;
        version = v; return true;
    }

    /// <summary>Compares only the first three numbers, the way the app shows its own version.</summary>
    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build)).CompareTo(new Version(current.Major, current.Minor, Math.Max(0, current.Build))) > 0;

    // ---------------------------------------------------------------- finding an update

    /// <summary>Reads GitHub's "latest release" document. Returns null when it is not newer than <paramref name="current"/> or is missing a file we need.</summary>
    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
            var tag = Str(root, "tag_name");
            if (!TryParseVersion(tag, out var version) || !IsNewer(version, current)) return null;

            string exeName = $"HSA-{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}-win-x64.exe";
            Uri? exe = null, sums = null; long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name"); var url = Str(a, "browser_download_url");
                    if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsTrustedDownload(u)) continue;
                    if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) { exe = u; size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0; }
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = u;
                }
            }
            if (exe is null || sums is null) return null;   // without the checksum file an update is never offered

            var page = Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var p) && IsTrustedPage(p) ? p : new Uri($"https://github.com/{Repo}/releases/latest");
            return new UpdateInfo(version, tag!, Str(root, "body") ?? "", exe, size, exeName, sums, page);
        }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Only this project's GitHub release downloads are trusted (or, when the test hook is set, the machine it points at).</summary>
    public static bool IsTrustedDownload(Uri u)
    {
        if (IsOverridden && Uri.TryCreate(Environment.GetEnvironmentVariable(OverrideEnvVar), UriKind.Absolute, out var o) && u.Host == o.Host && u.Port == o.Port) return true;
        return u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               && u.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTrustedPage(Uri u) => u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                                        && u.AbsolutePath.StartsWith($"/{Repo}/", StringComparison.OrdinalIgnoreCase);

    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no releases yet
        resp.EnsureSuccessStatusCode();
        return ParseRelease(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), current);
    }

    // ---------------------------------------------------------------- downloading

    /// <summary>The hash listed for <paramref name="fileName"/> in a "hash *name" checksum file, or null.</summary>
    public static string? FindHash(string sumsText, string fileName)
    {
        foreach (var line in sumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && m.Groups[2].Value.Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>A download that receives nothing for this long has stalled and is given up (and tried again by the caller). Shortened by tests.</summary>
    internal static TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    static async Task<T> WithinStallTimeout<T>(CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StallTimeout);
        try { return await action(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"GitHub did not answer for {StallTimeout.TotalSeconds:0} seconds."); }
    }

    /// <summary>A real HSA exe is about 160 MB; anything tiny is not one. Lowered by tests.</summary>
    internal static long MinExeSize = 1_000_000;

    public static string UpdatesFolder => Path.Combine(Settings.AppPaths.DataDir, "updates");

    /// <summary>Downloads and verifies the update; returns the path of the verified exe. A copy that is already downloaded and still verifies is reused.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default, string? folder = null)
    {
        folder ??= UpdatesFolder;
        Directory.CreateDirectory(folder);
        if (info.ExeSize < MinExeSize || info.ExeSize > 800_000_000) throw new InvalidOperationException("The release lists an unexpected file size, so the update was not downloaded.");

        var sums = await WithinStallTimeout(ct, t => http.GetStringAsync(info.SumsUrl, t)).ConfigureAwait(false);
        var expected = FindHash(sums, info.ExeName) ?? throw new InvalidOperationException($"The release has no checksum for {info.ExeName}, so the update was not installed.");

        var final = Path.Combine(folder, info.ExeName);
        if (File.Exists(final) && string.Equals(await HashAsync(final, ct).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase)) { VerifyProgram(final, info.Version); return final; }

        var part = final + ".partial";
        try
        {
            using (var resp = await WithinStallTimeout(ct, t => http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, t)).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buf = new byte[1 << 16]; long done = 0; int n, lastPct = -1;
                while ((n = await WithinStallTimeout(ct, async t => await src.ReadAsync(buf, t).ConfigureAwait(false)).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n; if (done > info.ExeSize) throw new InvalidDataException("The download is larger than the release says, so it was discarded.");
                    // one report per whole percent (not per 64 KB chunk): thousands of reports queue up on the UI thread and, when it is busy,
                    // delay the "download finished" step behind all of them
                    int pct = (int)(done * 100 / info.ExeSize);
                    if (pct != lastPct) { lastPct = pct; progress?.Report((double)done / info.ExeSize); }
                }
                if (done != info.ExeSize) throw new InvalidDataException("The download is incomplete, so it was discarded.");
            }
            var actual = await HashAsync(part, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file does not match the checksum published with the release, so it was discarded.");
            VerifyProgram(part, info.Version);
            File.Move(part, final, overwrite: true);
            return final;
        }
        catch { try { File.Delete(part); } catch { } throw; }
    }

    static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    /// <summary>A Windows program (starts with "MZ") whose product version is the announced one.</summary>
    internal static void VerifyProgram(string path, Version expected)
    {
        using (var fs = File.OpenRead(path)) { if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidDataException("The downloaded file is not a Windows program, so it was discarded."); }
        var pv = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (!TryParseVersion(pv?.Split('+', '-')[0], out var actual) || new Version(actual.Major, actual.Minor, Math.Max(0, actual.Build)) != new Version(expected.Major, expected.Minor, Math.Max(0, expected.Build)))
            throw new InvalidDataException($"The downloaded program is version {pv}, not {expected}, so it was discarded.");
    }
}
