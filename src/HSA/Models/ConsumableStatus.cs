namespace HSA.Models;

/// <summary>
/// One consumable (ink cartridge, toner, drum, etc.) on a printer, populated
/// by <c>IConsumableService</c> from the printer's prtMarkerSuppliesTable
/// (RFC 3805 Printer MIB).
/// </summary>
public sealed record ConsumableStatus
{
    /// <summary>Printer name (e.g., "HP LaserJet Pro M404dn").</summary>
    public string PrinterName { get; init; } = string.Empty;

    /// <summary>One-based index into the printer's supplies table.</summary>
    public int Index { get; init; }

    /// <summary>Human-readable description (e.g., "Black Cartridge HP CF258A").</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Cartridge part number, extracted from the description when present (e.g., "CF258A").</summary>
    public string? PartNumber { get; init; }

    /// <summary>Color: "black", "cyan", "magenta", "yellow", or "unknown".</summary>
    public string Color { get; init; } = "unknown";

    /// <summary>Class: 1=other, 2=ink, 3=toner, 4=inkCartridge, 5=inkRibbon, 6=wax, 7=fuser, 8=oil.</summary>
    public ConsumableClass Class { get; init; } = ConsumableClass.Other;

    /// <summary>Level as a percentage of max capacity (0-100). Null when unknown.</summary>
    public int? LevelPercent { get; init; }

    /// <summary>Maximum capacity, in printer-specific units. Null when unknown.</summary>
    public int? MaxCapacity { get; init; }

    /// <summary>Current raw level reading. Null when unknown.</summary>
    public int? CurrentLevel { get; init; }

    /// <summary>Rolled-up health status derived from <see cref="LevelPercent"/>.</summary>
    public ConsumableHealth Health { get; init; } = ConsumableHealth.Unknown;

    /// <summary>Last successful query time, UTC.</summary>
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public string LevelDisplay => LevelPercent is int p ? $"{p}%" : "â€”";
    public string HealthDisplay => Health switch
    {
        ConsumableHealth.Ok => "OK",
        ConsumableHealth.Low => "Low",
        ConsumableHealth.ReplaceSoon => "Replace soon",
        ConsumableHealth.ReplaceNow => "Replace now",
        ConsumableHealth.Empty => "Empty",
        _ => "Unknown"
    };
}

public enum ConsumableClass
{
    Other = 1,
    Ink = 2,
    Toner = 3,
    InkCartridge = 4,
    InkRibbon = 5,
    Wax = 6,
    Fuser = 7,
    Oil = 8
}

public enum ConsumableHealth
{
    Unknown,
    Ok,            // >= 50%
    Low,           // 20-49%
    ReplaceSoon,   // 5-19%
    ReplaceNow,    // 1-4%
    Empty          // 0%
}

public static class ConsumableHealthRules
{
    public static ConsumableHealth FromPercent(int? p) => p switch
    {
        null     => ConsumableHealth.Unknown,
        <= 0     => ConsumableHealth.Empty,
        <  5     => ConsumableHealth.ReplaceNow,
        < 20     => ConsumableHealth.ReplaceSoon,
        < 50     => ConsumableHealth.Low,
        _        => ConsumableHealth.Ok
    };
}
