namespace HSA.Models;

/// <summary>
/// One row in the driver store / a third-party driver package. Represents either:
///   - An OEM driver package (HP*.inf) in the Windows driver store, or
///   - A PnP driver class driver used by a connected device.
/// </summary>
public sealed record DriverInfo
{
    public string PublishedName { get; init; } = string.Empty;   // e.g. "oem42.inf"
    public string OriginalName { get; init; } = string.Empty;     // e.g. "hpygid40.inf"
    public string Provider { get; init; } = string.Empty;        // "HP"
    public string ClassName { get; init; } = string.Empty;       // "Printer"
    public string ClassGuid { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public DateTime? DriverDate { get; init; }
    public string InfPath { get; init; } = string.Empty;         // full path to installed INF
    public bool IsSigned { get; init; }
    public string? SignedBy { get; init; }
    public bool IsHp => Provider.Contains("HP", StringComparison.OrdinalIgnoreCase)
                     || OriginalName.Contains("hp", StringComparison.OrdinalIgnoreCase)
                     || InfPath.Contains("hp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names of printers that currently reference this driver.</summary>
    public IReadOnlyList<string> UsedByPrinters { get; init; } = Array.Empty<string>();

    public string Display => string.IsNullOrEmpty(OriginalName)
        ? PublishedName
        : $"{OriginalName}  ({Provider} {DriverVersion})";
    public string Subtitle => $"{(IsSigned ? "Signed" : "Unsigned")} · Class: {ClassName} · Published: {PublishedName}";
}
