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

    /// <summary>
    /// Pushes a firmware update to a network printer via the PWG 5100.11
    /// IPP System Services Update-Operation. The printer downloads the file
    /// from <paramref name="firmwareFileUri"/> and applies it. The protocol is
    /// standardized and does NOT bypass any signing - the printer verifies
    /// the firmware signature itself.
    /// </summary>
    /// <returns>
    /// A <see cref="FirmwarePushResult"/> describing the outcome. The printer
    /// may take several minutes to apply the firmware and reboot; the IPP
    /// response only acknowledges the request, not the final outcome.
    /// </returns>
    Task<FirmwarePushResult> PushUpdateAsync(
        PrinterInfo printer, Uri firmwareFileUri, CancellationToken ct = default);
}

/// <summary>Outcome of a PWG 5100.11 firmware update push.</summary>
public sealed record FirmwarePushResult(
    bool Requested,
    int IppStatusCode,
    string Message)
{
    /// <summary>True when the printer accepted the Update-Operation request.</summary>
    public bool Accepted => IppStatusCode == 0x0000;
    /// <summary>True when the printer responded with a "device busy" status
    /// (0x0500) - common when the printer is already processing.</summary>
    public bool DeviceBusy => IppStatusCode == 0x0500;
    /// <summary>True when the printer rejected the request (signature, format, etc.).</summary>
    public bool Rejected => IppStatusCode >= 0x0400 && IppStatusCode < 0x0500;
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

    public async Task<FirmwarePushResult> PushUpdateAsync(
        PrinterInfo printer, Uri firmwareFileUri, CancellationToken ct = default)
    {
        if (printer is null) throw new ArgumentNullException(nameof(printer));
        if (firmwareFileUri is null) throw new ArgumentNullException(nameof(firmwareFileUri));
        if (string.IsNullOrEmpty(printer.IpAddress))
        {
            return new FirmwarePushResult(false, -1,
                "Printer has no IP address. PWG 5100.11 Update requires a network path. " +
                "USB-only / WSD-USB printers can only be updated via the vendor's updater (see HP support link).");
        }

        var printerUri = $"ipp://{printer.IpAddress}/ipp/print";
        _log.LogInformation("PWG 5100.11 Update-Operation: {Printer} -> {Uri}",
            printer.Name, firmwareFileUri.AbsoluteUri);

        var status = await _ipp.UpdateFirmwareAsync(printerUri, firmwareFileUri, ct: ct);
        var message = status switch
        {
            0x0000 => "Printer accepted the Update-Operation. It will download and apply the firmware; the process may take several minutes and the printer may reboot.",
            0x0400 => "Printer returned client-error: bad request. The document URI may be invalid or the format unsupported.",
            0x0404 => "Printer returned 'document format not supported'. The URL must point to a firmware file the printer recognizes.",
            0x0500 => "Printer returned 'device busy' - typically means the printer is currently processing another job. Try again later.",
            0x0501 => "Printer returned 'not authorized' - the printer may be locked or the update URL is restricted.",
            0x0504 => "Printer returned 'device error' - check the printer's status page or front panel.",
            < 0    => "Network or IPP protocol error (no response from the printer).",
            _      => $"Printer returned IPP status 0x{status:X4}."
        };
        return new FirmwarePushResult(true, status, message);
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
