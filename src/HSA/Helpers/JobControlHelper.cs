using System.ComponentModel;
using System.Runtime.InteropServices;
using HSA.Native;

namespace HSA.Helpers;

internal static class JobControlHelper
{
    public static Task CancelJobAsync(string printerName, uint jobId) =>
        ControlJobAsync(printerName, jobId, Winspool.JOB_CONTROL_CANCEL);

    public static Task PauseJobAsync(string printerName, uint jobId) =>
        ControlJobAsync(printerName, jobId, Winspool.JOB_CONTROL_PAUSE);

    public static Task ResumeJobAsync(string printerName, uint jobId) =>
        ControlJobAsync(printerName, jobId, Winspool.JOB_CONTROL_RESUME);

    private static Task ControlJobAsync(string printerName, uint jobId, uint command) =>
        Task.Run(() =>
        {
            if (!Winspool.OpenPrinter(printerName, out var h, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!Winspool.SetJob(h, jobId, 0, IntPtr.Zero, command))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Winspool.ClosePrinter(h); }
        });
}
