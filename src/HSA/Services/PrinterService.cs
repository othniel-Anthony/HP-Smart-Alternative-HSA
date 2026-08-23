using System.Runtime.InteropServices;
using HSA.Models;
using HSA.Native;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

public interface IPrinterService
{
    Task<IReadOnlyList<PrinterInfo>> GetAllAsync(CancellationToken ct = default);
    Task<PrinterInfo?> GetAsync(string name, CancellationToken ct = default);
    Task SetAsDefaultAsync(string name, CancellationToken ct = default);
    Task RenameAsync(string oldName, string newName, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
    /// <summary>UAC-elevated fallback for <see cref="DeleteAsync"/>. Returns the prompt outcome.</summary>
    DeleteElevatedResult DeleteElevated(string name);
    /// <summary>Open Windows Settings → Printers (escape hatch when HSA can't delete a printer).</summary>
    void OpenWindowsPrintersSettings();
    Task OpenAdvancedPropertiesAsync(string name, IntPtr hwndOwner);
    Task OpenPrintingPreferencesAsync(string name, IntPtr hwndOwner);
    Task PrintTestPageAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<PrintJob>> GetJobsAsync(string name, CancellationToken ct = default);
    Task PauseQueueAsync(string name, CancellationToken ct = default);
    Task ResumeQueueAsync(string name, CancellationToken ct = default);
    Task PurgeQueueAsync(string name, CancellationToken ct = default);
}

/// <summary>Result of <see cref="IPrinterService.DeleteElevated"/>.</summary>
public enum DeleteElevatedOutcome
{
    Launched,
    Cancelled,
    Failed,
}

/// <summary>Outcome + optional error message for an elevated delete attempt.</summary>
public readonly record struct DeleteElevatedResult(DeleteElevatedOutcome Outcome, string? Error)
{
    public static DeleteElevatedResult Launched() => new(DeleteElevatedOutcome.Launched, null);
    public static DeleteElevatedResult Cancelled() => new(DeleteElevatedOutcome.Cancelled, null);
    public static DeleteElevatedResult Failed(string error) => new(DeleteElevatedOutcome.Failed, error);
}

/// <summary>
/// Spooler-backed printer service. Combines WMI for status with winspool for control operations.
/// </summary>
public sealed class PrinterService : IPrinterService
{
    private readonly ILogger<PrinterService> _log;

    public PrinterService(ILogger<PrinterService> log) => _log = log;

    public async Task<IReadOnlyList<PrinterInfo>> GetAllAsync(CancellationToken ct = default)
    {
        return await Task.Run<IReadOnlyList<PrinterInfo>>(() =>
        {
            var wmi = WmiHelper.QueryPrinters();
            var result = new List<PrinterInfo>(wmi.Count);
            foreach (var w in wmi)
                result.Add(MapFromWmi(w));
            return result;
        }, ct);
    }

    public async Task<PrinterInfo?> GetAsync(string name, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Task SetAsDefaultAsync(string name, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!Winspool.SetDefaultPrinter(name))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    $"SetDefaultPrinter failed for '{name}'.");
            _log.LogInformation("Set default printer to {Name}", name);
        }, ct);
    }

    public Task RenameAsync(string oldName, string newName, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New name cannot be empty.", nameof(newName));
            if (!Winspool.OpenPrinter(oldName, out var h, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                // SetPrinter at level 2 with PRINTER_INFO_2, leaving other fields identical.
                uint needed;
                Winspool.GetPrinter(h, Winspool.PRINTER_INFO_LEVEL_2, IntPtr.Zero, 0, out needed);
                if (needed == 0)
                    throw new InvalidOperationException("GetPrinter size query failed.");

                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!Winspool.GetPrinter(h, Winspool.PRINTER_INFO_LEVEL_2, buf, needed, out needed))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var info = Marshal.PtrToStructure<Winspool.PRINTER_INFO_2>(buf);
                    // pPrinterName is the first field; mutate in place.
                    var newNamePtr = Marshal.StringToHGlobalUni(newName);
                    try
                    {
                        var serverNameOffset = Marshal.OffsetOf<Winspool.PRINTER_INFO_2>(nameof(Winspool.PRINTER_INFO_2.pServerName)).ToInt32();
                        // pPrinterName is the second LPWStr field; place after the first pointer.
                        Marshal.WriteIntPtr(buf, (int)Marshal.SizeOf<IntPtr>(), newNamePtr);
                        if (!Winspool.SetPrinter(h, Winspool.PRINTER_INFO_LEVEL_2, buf, 0))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }
                    finally { Marshal.FreeHGlobal(newNamePtr); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Winspool.ClosePrinter(h); }
            _log.LogInformation("Renamed printer {Old} -> {New}", oldName, newName);
        }, ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!Winspool.OpenPrinter(name, out var h, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!Winspool.DeletePrinter(h))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Winspool.ClosePrinter(h); }
            _log.LogInformation("Deleted printer {Name}", name);
        }, ct);
    }

    /// <summary>
    /// Spawns a UAC-elevated PowerShell process to remove the named printer.
    /// Used as a fallback when <see cref="DeleteAsync"/> fails with
    /// "Access is denied" (Win32 error 5) — the spooler API requires admin
    /// rights to delete printers, but most users run HSA unelevated.
    /// Returns the UAC prompt outcome: Accepted means the elevated process
    /// was launched; UserCancelled means the user dismissed UAC.
    /// </summary>
    public DeleteElevatedResult DeleteElevated(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DeleteElevatedResult.Failed("Empty printer name.");
        try
        {
            // PowerShell with -Verb runas triggers UAC. The script uses
            // Remove-Printer (built-in cmdlet, present on Windows 10/11 /
            // Server 2016+). -ErrorAction Stop surfaces failures to the
            // caller. We capture $LASTEXITCODE into the exit code.
            //
            // IMPORTANT: pass the name as a positional argument (after -Command)
            // and use a here-string literal so the embedded printer name
            // can't be interpreted as PowerShell syntax. Names with single
            // quotes would break a single-quoted literal; escape via
            // -replace "'","''" so PS treats them as a literal char.
            var escaped = name.Replace("'", "''");
            var script =
                "$ErrorActionPreference = 'Stop'; " +
                $"try {{ Remove-Printer -Name '{escaped}' -ErrorAction Stop; exit 0 }} " +
                "catch { Write-Error $_.Exception.Message; exit 1 }";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                UseShellExecute = true,   // required for Verb=runas UAC
                Verb = "runas",
            };
            var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return DeleteElevatedResult.Failed("Failed to launch elevated PowerShell.");
            _log.LogInformation("Launched elevated Remove-Printer for {Name} (PID {Pid})", name, p.Id);
            return DeleteElevatedResult.Launched();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user dismissed the UAC prompt.
            return DeleteElevatedResult.Cancelled();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to launch elevated Remove-Printer for {Name}", name);
            return DeleteElevatedResult.Failed(ex.Message);
        }
    }

    /// <summary>Open Windows Settings → Bluetooth & devices → Printers & scanners.</summary>
    public void OpenWindowsPrintersSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:printers",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open Windows Settings → Printers");
            throw;
        }
    }

    public Task OpenAdvancedPropertiesAsync(string name, IntPtr hwndOwner)
    {
        return Task.Run(() =>
        {
            if (!Winspool.OpenPrinter(name, out var h, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint needed;
                Winspool.GetPrinter(h, Winspool.PRINTER_INFO_LEVEL_2, IntPtr.Zero, 0, out needed);
                if (needed == 0) return;

                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!Winspool.GetPrinter(h, Winspool.PRINTER_INFO_LEVEL_2, buf, needed, out needed))
                        return;
                    // DocumentProperties with DM_PROMPT shows the printer-specific properties dialog
                    // (this is the "Advanced" sheet the user can reach from printui.dll).
                    Winspool.DocumentProperties(hwndOwner, h, name, IntPtr.Zero, IntPtr.Zero,
                        Winspool.DM_PROMPT);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Winspool.ClosePrinter(h); }
        });
    }

    public Task OpenPrintingPreferencesAsync(string name, IntPtr hwndOwner)
    {
        return Task.Run(() =>
        {
            // "Printing Preferences" is the per-user document defaults dialog.
            // The cleanest way to open it is to launch the printui.exe command-line UI:
            //   rundll32.exe printui.dll,PrintUIEntry /e /n "PrinterName"
            // For full preferences use the standard Windows shell verb.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /e /n \"{name}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        });
    }

    public Task PrintTestPageAsync(string name, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /k /n \"{name}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }, ct);
    }

    public Task<IReadOnlyList<PrintJob>> GetJobsAsync(string name, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<PrintJob>>(() =>
        {
            var list = new List<PrintJob>();
            if (!Winspool.OpenPrinter(name, out var h, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint needed, returned;
                Winspool.EnumJobs(h, 0, 255, 1, IntPtr.Zero, 0, out needed, out returned);
                if (needed == 0) return list;

                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!Winspool.EnumJobs(h, 0, 255, 1, buf, needed, out needed, out returned))
                        return list;
                    var size = Marshal.SizeOf<Winspool.JOB_INFO_1>();
                    for (int i = 0; i < returned; i++)
                    {
                        var ptr = IntPtr.Add(buf, i * size);
                        var ji = Marshal.PtrToStructure<Winspool.JOB_INFO_1>(ptr);
                        list.Add(new PrintJob
                        {
                            Id = ji.JobId,
                            PrinterName = ji.pPrinterName,
                            DocumentName = ji.pDocument,
                            UserName = ji.pUserName,
                            MachineName = ji.pMachineName,
                            Status = (PrintJobStatus)ji.Status,
                            TotalPages = ji.TotalPages,
                            PagesPrinted = ji.PagesPrinted,
                            SubmittedTime = new DateTime(
                                ji.Submitted.wYear, ji.Submitted.wMonth, ji.Submitted.wDay,
                                ji.Submitted.wHour, ji.Submitted.wMinute, ji.Submitted.wSecond,
                                DateTimeKind.Local),
                            DriverName = string.Empty,
                            PortName = string.Empty
                        });
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Winspool.ClosePrinter(h); }
            return list;
        }, ct);
    }

    public Task PauseQueueAsync(string name, CancellationToken ct = default)
    {
        return Task.Run(() => WithOpenPrinter(name, h =>
        {
            if (!Winspool.SetPrinter(h, 0, IntPtr.Zero, Winspool.PRINTER_CONTROL_PAUSE))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }), ct);
    }

    public Task ResumeQueueAsync(string name, CancellationToken ct = default)
    {
        return Task.Run(() => WithOpenPrinter(name, h =>
        {
            if (!Winspool.SetPrinter(h, 0, IntPtr.Zero, Winspool.PRINTER_CONTROL_RESUME))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }), ct);
    }

    public Task PurgeQueueAsync(string name, CancellationToken ct = default)
    {
        return Task.Run(() => WithOpenPrinter(name, h =>
        {
            if (!Winspool.SetPrinter(h, 0, IntPtr.Zero, Winspool.PRINTER_CONTROL_PURGE))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }), ct);
    }

    private static void WithOpenPrinter(string name, Action<IntPtr> action)
    {
        if (!Winspool.OpenPrinter(name, out var h, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { action(h); }
        finally { Winspool.ClosePrinter(h); }
    }

    private static PrinterInfo MapFromWmi(Win32PrinterRow w)
    {
        var status = MapStatus(w);
        var connection = w.IsNetwork ? PrinterConnectionKind.Network :
                         w.IsShared ? PrinterConnectionKind.Shared :
                         w.IsLocal ? PrinterConnectionKind.Local : PrinterConnectionKind.Unknown;
        return new PrinterInfo
        {
            Name = w.Name,
            DeviceId = string.IsNullOrWhiteSpace(w.DeviceId) ? w.Name : w.DeviceId,
            ShareName = w.ShareName,
            PortName = w.PortName,
            DriverName = w.DriverName,
            Manufacturer = w.Manufacturer,
            Model = w.Description,
            IsNetworkPrinter = w.IsNetwork,
            Connection = connection,
            Status = status,
            StatusMessage = w.Status,
            IsDefault = w.IsDefault,
            IsShared = w.IsShared,
            Location = w.Location,
            Comment = w.Comment,
        };
    }

    private static PrinterStatus MapStatus(Win32PrinterRow w)
    {
        // WMI DetectedErrorState (0 = Unknown, 1 = Other, 2 = OK, 7 = Configuration, etc.)
        if (string.Equals(w.Status, "Unknown", StringComparison.OrdinalIgnoreCase)) return PrinterStatus.Unknown;
        if (w.WorkOffline) return PrinterStatus.Offline;

        // The ExtendedPrinterStatus field is more granular and most HP drivers populate it.
        return w.ExtendedPrinterStatus switch
        {
            0 => PrinterStatus.Unknown,
            1 => PrinterStatus.Unknown,
            2 => PrinterStatus.Paused,
            3 => PrinterStatus.Error,
            4 => PrinterStatus.PaperJam,
            5 => PrinterStatus.OutOfPaper,
            6 => PrinterStatus.NeedsUserIntervention,
            7 => PrinterStatus.Other,
            8 => PrinterStatus.Other,
            9 => PrinterStatus.Other,
            10 => PrinterStatus.TonerLow,
            11 => PrinterStatus.Other,
            12 => PrinterStatus.Ready,
            13 => PrinterStatus.Ready,
            14 => PrinterStatus.Ready,
            15 => PrinterStatus.Initializing,
            16 => PrinterStatus.WarmingUp,
            _ => PrinterStatus.Unknown
        };
    }
}
