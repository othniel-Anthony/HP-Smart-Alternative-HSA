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
                     || DriverName.Contains("HP", StringComparison.OrdinalIgnoreCase)
                     || Name.Contains("HP", StringComparison.OrdinalIgnoreCase)
                     || Name.Contains("Hewlett", StringComparison.OrdinalIgnoreCase);

    public PrinterConnectionKind Connection { get; init; } = PrinterConnectionKind.Unknown;
    public PrinterStatus Status { get; set; } = PrinterStatus.Unknown;
    public string StatusMessage { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsShared { get; init; }
    public string Location { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public string? IpAddress { get; init; }

    // ---- Setable extensions (set by the view model after creation) ----

    private Uri? _modelImageUri;
    public Uri? ModelImageUri
    {
        get => _modelImageUri;
        set { if (_modelImageUri != value) { _modelImageUri = value; OnPropertyChanged(); } }
    }

    private string? _modelFamily;
    public string? ModelFamily
    {
        get => _modelFamily;
        set { if (_modelFamily != value) { _modelFamily = value; OnPropertyChanged(); } }
    }

    // ---- Background-loaded by PrintersViewModel after a refresh ----
    // Set by a per-printer background task once SNMP consumable data arrives.
    // Empty until then; the UI hides the chip row when the list is empty.
    private IReadOnlyList<ConsumableStatus> _consumables = Array.Empty<ConsumableStatus>();
    public IReadOnlyList<ConsumableStatus> Consumables
    {
        get => _consumables;
        set { if (!ReferenceEquals(_consumables, value)) { _consumables = value ?? Array.Empty<ConsumableStatus>(); OnPropertyChanged(); } }
    }

    // ---- Read-only after construction ----

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
