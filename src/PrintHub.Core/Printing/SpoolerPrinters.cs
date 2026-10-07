using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintHub.Core.Printing;

public sealed record SpoolerPrinter(string Name, string Port, string Driver, string Location, uint Status, uint Attributes, uint Jobs, bool IsDefault)
{
    public bool IsOffline => (Status & 0x80) != 0 || (Attributes & 0x400) != 0;
    public string StatusText => Describe(Status, Attributes);

    static string Describe(uint s, uint attr)
    {
        if ((attr & 0x400) != 0) return "Offline (work offline)";
        var parts = new List<string>();
        void F(uint f, string t) { if ((s & f) != 0) parts.Add(t); }
        F(0x1, "Paused"); F(0x2, "Error"); F(0x4, "Deleting"); F(0x8, "Paper jam"); F(0x10, "Out of paper"); F(0x20, "Manual feed");
        F(0x40, "Paper problem"); F(0x80, "Offline"); F(0x100, "I/O active"); F(0x200, "Busy"); F(0x400, "Printing"); F(0x800, "Output bin full");
        F(0x1000, "Not available"); F(0x2000, "Waiting"); F(0x4000, "Processing"); F(0x8000, "Initializing"); F(0x10000, "Warming up");
        F(0x20000, "Toner low"); F(0x40000, "No toner"); F(0x80000, "Page punt"); F(0x100000, "User intervention"); F(0x200000, "Out of memory");
        F(0x400000, "Door open");
        return parts.Count == 0 ? "Ready" : string.Join(", ", parts);
    }
}

/// <summary>Windows print queues: enumeration, test page, preferences and queue UI.</summary>
public static class SpoolerPrinters
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRINTER_INFO_2
    {
        public string pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
        public IntPtr pDevMode;
        public string pSepFile, pPrintProcessor, pDatatype, pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buf, uint cb, out uint needed, out uint returned);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDefaultPrinter(StringBuilder? buf, ref uint size);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDefaultPrinter(string name);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool OpenPrinter(string name, out IntPtr handle, ref PRINTER_DEFAULTS defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool SetPrinter(IntPtr handle, uint level, IntPtr info, uint command);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRINTER_DEFAULTS { public IntPtr pDatatype; public IntPtr pDevMode; public uint DesiredAccess; }

    /// <summary>Cancel every job waiting on a Windows queue. Needs permission to manage the printer; returns false (with the Windows error) when refused.</summary>
    public static bool Purge(string printer, out string error)
    {
        error = "";
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = 0x0004 /* PRINTER_ACCESS_ADMINISTER */ };
        if (!OpenPrinter(printer, out var h, ref defaults)) { error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
        try
        {
            if (SetPrinter(h, 0, IntPtr.Zero, 3 /* PRINTER_CONTROL_PURGE */)) return true;
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        finally { ClosePrinter(h); }
    }

    public static string? GetDefault()
    {
        uint size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0) return null;
        var sb = new StringBuilder((int)size);
        return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    public static bool SetDefault(string name) => SetDefaultPrinter(name);

    public static List<SpoolerPrinter> List()
    {
        const uint LOCAL = 2, CONNECTIONS = 4;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        // The size Windows asks for and the list itself are two calls: a queue added or removed in between (a repair bench with many printers being plugged in) makes the
        // second call fail with "buffer too small". That used to come back as an empty list, so printers went missing for a search. Ask again with the new size.
        EnumPrinters(LOCAL | CONNECTIONS, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return new List<SpoolerPrinter>();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            uint cb = needed + needed / 4 + 4096;   // room for queues added meanwhile
            var buf = Marshal.AllocHGlobal((int)cb);
            try
            {
                if (EnumPrinters(LOCAL | CONNECTIONS, null, 2, buf, cb, out var need2, out var count))
                {
                    var result = new List<SpoolerPrinter>((int)count);
                    var def = GetDefault();
                    int size = Marshal.SizeOf<PRINTER_INFO_2>();
                    for (int i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                        result.Add(new SpoolerPrinter(info.pPrinterName ?? "", info.pPortName ?? "", info.pDriverName ?? "",
                            info.pLocation ?? "", info.Status, info.Attributes, info.cJobs,
                            string.Equals(info.pPrinterName, def, StringComparison.OrdinalIgnoreCase)));
                    }
                    return result;
                }
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_INSUFFICIENT_BUFFER) throw new System.ComponentModel.Win32Exception(err);
                needed = Math.Max(need2, needed);
            }
            finally { Marshal.FreeHGlobal(buf); }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("The Windows printer list kept changing while it was being read.");
    }

    /// <summary>Extract an IPv4 address from port names such as "IP_192.168.1.5" or "192.168.1.5_1".</summary>
    public static string? AddressFromPort(string port)
    {
        var m = System.Text.RegularExpressions.Regex.Match(port, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static void PrintTestPage(string printer) => PrintUi($"/k /n\"{printer}\"");
    public static void OpenPreferences(string printer) => PrintUi($"/e /n\"{printer}\"");
    public static void OpenQueue(string printer) => Process.Start(new ProcessStartInfo("rundll32.exe", $"printui.dll,PrintUIEntry /o /n\"{printer}\"") { UseShellExecute = false });
    public static void OpenProperties(string printer) => PrintUi($"/p /n\"{printer}\"");

    static void PrintUi(string args) =>
        Process.Start(new ProcessStartInfo("rundll32.exe", "printui.dll,PrintUIEntry " + args) { UseShellExecute = false });
}
