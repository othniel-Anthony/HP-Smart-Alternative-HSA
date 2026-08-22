using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HSA.Models;
using HSA.Native;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

public interface IFirmwareService
{
    /// <summary>Reads firmware version from a network printer via SNMP, or falls back to EWS.</summary>
    Task<FirmwareInfo> DetectAsync(PrinterInfo printer, CancellationToken ct = default);

    /// <summary>Returns the deep link to HP's support page for a given model name.</summary>
    Uri BuildHpSupportUri(string modelIdentifier);

    /// <summary>Opens the HP support page in the user's default browser.</summary>
    void OpenHpSupportPage(string modelIdentifier);
}

/// <summary>
/// Firmware service: detects the current firmware version and produces the appropriate
/// update flow per the hybrid policy:
///
///   * Network printers with SNMP or IPP reachable: detect version, attempt IPP System
///     Services Update (PWG 5100.11) where supported, otherwise deep-link to HP support.
///   * USB-only printers: skip version detection, deep-link to HP support.
///   * Printers not detected at all: still produce a model-based deep link when possible.
/// </summary>
public sealed class FirmwareService : IFirmwareService
{
    private readonly ILogger<FirmwareService> _log;
    private readonly IppClient _ipp;
    private readonly SnmpClient _snmp;

    public FirmwareService(ILogger<FirmwareService> log)
    {
        _log = log;
        _ipp = new IppClient();
        _snmp = new SnmpClient();
    }

    public async Task<FirmwareInfo> DetectAsync(PrinterInfo printer, CancellationToken ct = default)
    {
        var info = new FirmwareInfo
        {
            PrinterName = printer.Name,
            ModelIdentifier = GuessModelIdentifier(printer),
            IpAddress = printer.IpAddress
        };

        if (!printer.IsNetworkPrinter || string.IsNullOrEmpty(printer.IpAddress))
        {
            // No network path — fall back to HP support link.
            info.DetectionMethod = FirmwareDetectionMethod.Unknown;
            info.UpdateCapability = FirmwareUpdateCapability.HpSupportLink;
            info.HpSupportUrl = BuildHpSupportUri(info.ModelIdentifier ?? string.Empty).ToString();
            return info;
        }

        if (!IPAddress.TryParse(printer.IpAddress, out var ip))
        {
            info.DetectionMethod = FirmwareDetectionMethod.Unknown;
            info.UpdateCapability = FirmwareUpdateCapability.HpSupportLink;
            info.HpSupportUrl = BuildHpSupportUri(info.ModelIdentifier ?? string.Empty).ToString();
            return info;
        }

        // 1) Try SNMP
        var oidResults = await _snmp.GetManyAsync(ip, new[]
        {
            SnmpClient.SysDescr,
            SnmpClient.PrtGeneralPrinterName,
            SnmpClient.HpLaserJetFirmware
        }, ct);
        if (oidResults.TryGetValue(SnmpClient.HpLaserJetFirmware, out var fw) && !string.IsNullOrWhiteSpace(fw))
        {
            info.CurrentVersion = fw;
            info.DetectionMethod = FirmwareDetectionMethod.Snmp;
            info.UpdateCapability = FirmwareUpdateCapability.HpSupportLink;
            info.HpSupportUrl = BuildHpSupportUri(info.ModelIdentifier ?? string.Empty).ToString();
            return info;
        }
        if (oidResults.TryGetValue(SnmpClient.PrtGeneralPrinterName, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            info.ModelIdentifier = name;
        }

        // 2) Try IPP
        var ippReachable = await IppClient.IsReachableAsync(ip, IppClient.DefaultIppPort, 2000, ct);
        if (ippReachable)
        {
            var uri = $"ipp://{ip}/ipp/print";
            var attrs = await _ipp.GetPrinterAttributesAsync(uri, new[]
            {
                "printer-firmware-version",
                "printer-firmware-name",
                "printer-name",
                "printer-make-and-model"
            }, ct);
            if (attrs is not null)
            {
                info.CurrentVersion = attrs.GetString("printer-firmware-version");
                if (string.IsNullOrEmpty(info.ModelIdentifier))
                {
                    var m = attrs.GetString("printer-make-and-model");
                    if (!string.IsNullOrEmpty(m)) info.ModelIdentifier = m;
                }
                info.DetectionMethod = FirmwareDetectionMethod.Ipp;
                info.UpdateCapability = FirmwareUpdateCapability.IppSystemServices;
                info.HpSupportUrl = BuildHpSupportUri(info.ModelIdentifier ?? string.Empty).ToString();
                return info;
            }
        }

        // 3) Fallback
        info.DetectionMethod = FirmwareDetectionMethod.Unknown;
        info.UpdateCapability = FirmwareUpdateCapability.HpSupportLink;
        info.HpSupportUrl = BuildHpSupportUri(info.ModelIdentifier ?? string.Empty).ToString();
        return info;
    }

    public Uri BuildHpSupportUri(string modelIdentifier)
    {
        var model = NormalizeModel(modelIdentifier);
        // HP's search URL pattern: https://support.hp.com/drivers?product=...&pattern=...
        // We use the search-by-name pattern which is stable across HP's site redesigns.
        var url = "https://support.hp.com/drivers/printer-firmware";
        if (!string.IsNullOrEmpty(model))
            url = $"https://support.hp.com/drivers?pattern={Uri.EscapeDataString(model)}&product=&filter=&lang=en&cc=us";
        return new Uri(url);
    }

    public void OpenHpSupportPage(string modelIdentifier)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BuildHpSupportUri(modelIdentifier).ToString(),
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open HP support page for {Model}", modelIdentifier);
        }
    }

    private static string? GuessModelIdentifier(PrinterInfo p)
    {
        // Prefer explicit model description; fall back to driver name; drop "HP " prefix.
        var candidates = new[] { p.Model, p.DriverName, p.Name };
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var trimmed = c.Trim();
            if (trimmed.StartsWith("HP ", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(3);
            if (Regex.IsMatch(trimmed, @"[A-Za-z]\w*\s*\w+", RegexOptions.IgnoreCase))
                return trimmed;
        }
        return null;
    }

    private static string NormalizeModel(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
