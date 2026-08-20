namespace HSA.Models;

public sealed class PrintJob
{
    public uint Id { get; init; }
    public string PrinterName { get; init; } = string.Empty;
    public string DocumentName { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public PrintJobStatus Status { get; set; }
    public uint PagesPrinted { get; init; }
    public uint TotalPages { get; init; }
    public DateTime SubmittedTime { get; init; }
    public string PortName { get; init; } = string.Empty;
    public string DriverName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }

    public string Display => $"{DocumentName} ({UserName})";
    public string StatusDisplay => Status.ToString();
    public string ProgressDisplay => TotalPages > 0
        ? $"{PagesPrinted}/{TotalPages}"
        : $"{PagesPrinted} pages";
}

[Flags]
public enum PrintJobStatus
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    Deleting = 0x00000004,
    Spooling = 0x00000008,
    Printing = 0x00000010,
    Offline = 0x00000020,
    Paperout = 0x00000040,
    Printed = 0x00000080,
    Blocked = 0x00000200,
    UserIntervention = 0x00000400,
    Restart = 0x00000800,
    Complete = 0x00001000,
    Retained = 0x00002000,
    RenderingLocally = 0x00004000,
    All = 0x00007FFF
}
