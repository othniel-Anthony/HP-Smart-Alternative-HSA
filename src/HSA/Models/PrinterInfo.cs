using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HSA.Models;

/// <summary>
/// Represents a single printer visible to the local Windows print spooler.
/// Combines info from the spooler API (name, port, driver), WMI (status, capabilities),
/// and (for network printers) SNMP/IPP (model, firmware, consumables).
/// </summary>
public sealed class PrinterInfo : INotifyPropertyChanged
{
    public string Name { get; init; } = string.Empty;
    public string ShareName { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public string DriverName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public bool IsNetworkPrinter { get; init; }
    public bool IsHp => Manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase)
                     || Model.Contains("HP", StringComparison.OrdinalIgnoreCase)
                     || DriverName.Contains("HP", StringComparison.OrdinalIgnoreCase);

    public PrinterConnectionKind Connection { get; init; } = PrinterConnectionKind.Unknown;
    public PrinterStatus Status { get; set; } = PrinterStatus.Unknown;
    public string StatusMessage { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsShared { get; init; }
    public string Location { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public string? IpAddress { get; init; }

    // Consumables (when known)
    public IReadOnlyList<ConsumableLevel> Consumables { get; init; } = Array.Empty<ConsumableLevel>();

    // Firmware (when known)
    public string? FirmwareVersion { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum PrinterConnectionKind
{
    Unknown,
    Local,
    Network,
    Shared
}

public enum PrinterStatus
{
    Unknown,
    Ready,
    Printing,
    Paused,
    Error,
    Offline,
    OutOfMemory,
    OutOfPaper,
    PaperJam,
    NeedsUserIntervention,
    Initializing,
    WarmingUp,
    TonerLow,
    Other
}

public sealed record ConsumableLevel(string Name, string? Color, int? LevelPercent, string? PartNumber)
{
    public string Display =>
        LevelPercent is int p ? $"{Name} â€” {p}%" : Name;
}
