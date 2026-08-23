using System.IO;
using System.IO.Compression;
using System.Net.Http;
using HSA.Models;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// A downloaded driver file - either a direct INF (ready to install) or a
/// compressed package (ZIP / CAB / EXE) that may contain one or more INFs.
/// </summary>
public sealed record DownloadedDriver(
    string FilePath,
    long SizeBytes,
    IReadOnlyList<string> ContainedInfs,
    bool IsContainer);

/// <summary>
/// Downloads driver files (HP driver packages, MS Update catalog downloads,
/// etc.) to a local folder and extracts INFs from common container formats
/// (ZIP, CAB). EXE installers are not auto-extracted (they typically require
/// running the installer to lay down files), but we still surface them so the
/// user can decide what to do.
/// </summary>
public sealed class DriverDownloader
{
    private readonly ILogger<DriverDownloader> _log;

    /// <summary>Folder where downloaded drivers are stored. Created on first use.</summary>
    public static string DefaultDownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HSA", "Downloads");

    public DriverDownloader(ILogger<DriverDownloader> log)
    {
        _log = log;
    }

    public async Task<DownloadedDriver> DownloadAsync(
        string url, string? suggestedFileName = null,
        IProgress<(long Done, long Total, double Percent)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("URL is not absolute.", nameof(url));

        Directory.CreateDirectory(DefaultDownloadFolder);
        var fileName = suggestedFileName
            ?? Path.GetFileName(uri.LocalPath)
            ?? $"driver-{Guid.NewGuid():N}.bin";
        var dest = Path.Combine(DefaultDownloadFolder, fileName);
        // If we'd clobber an existing download, suffix with -1, -2, ...
        int suffix = 1;
        while (File.Exists(dest))
        {
            var ext = Path.GetExtension(fileName);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            dest = Path.Combine(DefaultDownloadFolder, $"{stem}-{suffix}{ext}");
            suffix++;
        }

        _log.LogInformation("Downloading {Url} -> {Dest}", url, dest);

        var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int lastReportedPercent = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await src.ReadAsync(buffer, ct);
            if (read <= 0) break;
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                var pct = (int)(100.0 * done / total);
                if (pct != lastReportedPercent)
                {
                    progress?.Report((done, total, pct));
                    lastReportedPercent = pct;
                }
            }
        }
        progress?.Report((done, total > 0 ? total : done, 100));

        var size = new FileInfo(dest).Length;
        var infs = new List<string>();
        var isContainer = false;
        try
        {
            if (string.Equals(Path.GetExtension(dest), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                isContainer = true;
                infs.AddRange(ExtractInfsFromZip(dest));
            }
            else if (string.Equals(Path.GetExtension(dest), ".inf", StringComparison.OrdinalIgnoreCase))
            {
                infs.Add(dest);
            }
            // .cab / .exe: we don't extract automatically. EXE installers require
            // the vendor's installer to lay down files; CABs require expand.exe.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to scan downloaded file for INFs");
        }

        return new DownloadedDriver(dest, size, infs, isContainer);
    }

    /// <summary>
    /// Extracts a ZIP into a sibling folder next to the file (same name without
    /// extension) and returns any .inf files found inside. Doesn't recurse into
    /// nested archives - that path needs 7z or similar, not built-in.
    /// </summary>
    public static IEnumerable<string> ExtractInfsFromZip(string zipPath)
    {
        var extractDir = Path.Combine(
            Path.GetDirectoryName(zipPath) ?? ".",
            Path.GetFileNameWithoutExtension(zipPath) + "-extracted");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        return Directory.EnumerateFiles(extractDir, "*.inf", SearchOption.AllDirectories);
    }
}
