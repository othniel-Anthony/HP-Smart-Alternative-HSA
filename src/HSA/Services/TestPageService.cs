using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// v0.2.14: provides the path to the bundled HP color-test-page PDF that
/// the "Test print" button prints. The PDF is shipped as an embedded
/// resource and extracted to <c>%LOCALAPPDATA%\HSA\test-page.pdf</c> on
/// first run so it survives alongside the user's other app data.
/// </summary>
public sealed class TestPageService
{
    private readonly ILogger<TestPageService> _log;
    private string? _path;

    public TestPageService(ILogger<TestPageService> log)
    {
        _log = log;
    }

    /// <summary>
    /// Returns the path to the bundled test-page PDF, extracting it from
    /// the embedded resource on first call. Returns null only if the
    /// resource is missing (e.g. a slimmed-down build).
    /// </summary>
    public string? GetTestPagePath()
    {
        if (_path is not null && File.Exists(_path)) return _path;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HSA");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "test-page.pdf");

        if (!File.Exists(target))
        {
            var asm = typeof(TestPageService).Assembly;
            using var src = asm.GetManifestResourceStream("HSA.Resources.test-page.pdf");
            if (src is null)
            {
                _log.LogError("Embedded resource HSA.Resources.test-page.pdf is missing.");
                return null;
            }
            using var dst = File.Create(target);
            src.CopyTo(dst);
            _log.LogInformation("Extracted bundled test-page PDF to {Path} ({Bytes} bytes)",
                target, new FileInfo(target).Length);
        }

        _path = target;
        return _path;
    }
}
