using System.Runtime.InteropServices;

namespace HSA.Native;

/// <summary>
/// P/Invoke surface for winspool.drv â€” the Windows Print Spooler API.
/// Reference: https://learn.microsoft.com/en-us/windows/win32/printdocs/print-spooler-api
/// </summary>
internal static class Winspool
{
    private const string Dll = "winspool.drv";

    // ---- Enumeration flags ----
    public const uint PRINTER_ENUM_LOCAL = 0x00000002;
    public const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;
    public const uint PRINTER_ENUM_FAVORITE = 0x00000004;
    public const uint PRINTER_ENUM_NAME = 0x00000008;
    public const uint PRINTER_ENUM_REMOTE = 0x00000010;
    public const uint PRINTER_ENUM_SHARED = 0x00000020;
    public const uint PRINTER_ENUM_NETWORK = 0x00000040;
    public const uint PRINTER_ENUM_EXPAND = 0x00004000;
    public const uint PRINTER_ENUM_CONTAINER = 0x00008000;
    public const uint PRINTER_ENUM_ICON1 = 0x00010000;
    public const uint PRINTER_ENUM_ICON2 = 0x00020000;
    public const uint PRINTER_ENUM_ICON3 = 0x00040000;
    public const uint PRINTER_ENUM_ICON4 = 0x00080000;
    public const uint PRINTER_ENUM_ICON5 = 0x00100000;
    public const uint PRINTER_ENUM_ICON6 = 0x00200000;
    public const uint PRINTER_ENUM_ICON7 = 0x00400000;
    public const uint PRINTER_ENUM_ICON8 = 0x00800000;
    public const uint PRINTER_ENUM_HIDE = 0x01000000;

    // ---- Printer info levels ----
    public const uint PRINTER_INFO_LEVEL_1 = 1;
    public const uint PRINTER_INFO_LEVEL_2 = 2;
    public const uint PRINTER_INFO_LEVEL_3 = 3;
    public const uint PRINTER_INFO_LEVEL_4 = 4;
    public const uint PRINTER_INFO_LEVEL_5 = 5;
    public const uint PRINTER_INFO_LEVEL_6 = 6;
    public const uint PRINTER_INFO_LEVEL_7 = 7;

    // ---- PRINTER_INFO_2 fields (PRINTER_STATUS_*) ----
    public const uint PRINTER_STATUS_PAUSED = 0x00000001;
    public const uint PRINTER_STATUS_ERROR = 0x00000002;
    public const uint PRINTER_STATUS_PENDING_DELETION = 0x00000004;
    public const uint PRINTER_STATUS_PAPER_JAM = 0x00000008;
    public const uint PRINTER_STATUS_PAPER_OUT = 0x00000010;
    public const uint PRINTER_STATUS_MANUAL_FEED = 0x00000020;
    public const uint PRINTER_STATUS_PAPER_PROBLEM = 0x00000040;
    public const uint PRINTER_STATUS_OFFLINE = 0x00000080;
    public const uint PRINTER_STATUS_IO_ACTIVE = 0x00000100;
    public const uint PRINTER_STATUS_BUSY = 0x00000200;
    public const uint PRINTER_STATUS_PRINTING = 0x00000400;
    public const uint PRINTER_STATUS_OUTPUT_BIN_FULL = 0x00000800;
    public const uint PRINTER_STATUS_NOT_AVAILABLE = 0x00001000;
    public const uint PRINTER_STATUS_WAITING = 0x00002000;
    public const uint PRINTER_STATUS_PROCESSING = 0x00004000;
    public const uint PRINTER_STATUS_INITIALIZING = 0x00008000;
    public const uint PRINTER_STATUS_WARMING_UP = 0x00010000;
    public const uint PRINTER_STATUS_TONER_LOW = 0x00020000;
    public const uint PRINTER_STATUS_NO_TONER = 0x00040000;
    public const uint PRINTER_STATUS_PAGE_PUNT = 0x00080000;
    public const uint PRINTER_STATUS_USER_INTERVENTION = 0x00100000;
    public const uint PRINTER_STATUS_OUT_OF_MEMORY = 0x00200000;
    public const uint PRINTER_STATUS_DOOR_OPEN = 0x00400000;
    public const uint PRINTER_STATUS_SERVER_UNKNOWN = 0x00800000;
    public const uint PRINTER_STATUS_POWER_SAVE = 0x01000000;

    // ---- OpenPrinter access masks ----
    public const uint PRINTER_ACCESS_ADMINISTER = 0x00000004;
    public const uint PRINTER_ACCESS_USE = 0x00000008;
    public const uint PRINTER_ACCESS_MANAGE_LIMITED = 0x00000040;

    // ---- SetJob commands ----
    public const uint JOB_CONTROL_PAUSE = 1;
    public const uint JOB_CONTROL_RESUME = 2;
    public const uint JOB_CONTROL_CANCEL = 3;
    public const uint JOB_CONTROL_RESTART = 4;
    public const uint JOB_CONTROL_DELETE = 5;
    public const uint JOB_CONTROL_SENT_TO_PRINTER = 6;
    public const uint JOB_CONTROL_LAST_PAGE_EJECTED = 7;

    // ---- SetPrinter commands ----
    public const uint PRINTER_CONTROL_PAUSE = 1;
    public const uint PRINTER_CONTROL_RESUME = 2;
    public const uint PRINTER_CONTROL_PURGE = 3;
    public const uint PRINTER_CONTROL_SET_STATUS = 4;

    // ---- DEVMODE fields ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_INFO_1
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDescription;
        [MarshalAs(UnmanagedType.LPWStr)] public string pName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pComment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_INFO_2
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pServerName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pShareName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pPortName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDriverName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string pLocation;
        public IntPtr pDevMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string pSepFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pPrintProcessor;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
        [MarshalAs(UnmanagedType.LPWStr)] public string pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_INFO_4
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pServerName;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOB_INFO_1
    {
        public uint JobId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pMachineName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pUserName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocument;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
        [MarshalAs(UnmanagedType.LPWStr)] public string pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    // ---- API surface ----
    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumPrintersW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumPrinters(
        uint Flags,
        [MarshalAs(UnmanagedType.LPWStr)] string? Name,
        uint Level,
        IntPtr pPrinterEnum,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenPrinter(
        [MarshalAs(UnmanagedType.LPWStr)] string pPrinterName,
        out IntPtr hPrinter,
        IntPtr pDefault);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ClosePrinter")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPrinter(
        IntPtr hPrinter,
        uint Level,
        IntPtr pPrinter,
        uint cbBuf,
        out uint pcbNeeded);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPrinter(
        IntPtr hPrinter,
        uint Level,
        IntPtr pPrinter,
        uint Command);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetDefaultPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDefaultPrinter(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPrinter);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DeletePrinter")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeletePrinter(IntPtr hPrinter);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "AddPrinterW")]
    public static extern IntPtr AddPrinter(
        [MarshalAs(UnmanagedType.LPWStr)] string? pName,
        uint Level,
        IntPtr pPrinter);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumJobsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumJobs(
        IntPtr hPrinter,
        uint FirstJob,
        uint NumJobs,
        uint Level,
        IntPtr pJob,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetJobW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetJob(
        IntPtr hPrinter,
        uint JobId,
        uint Level,
        IntPtr pJob,
        uint Command);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DocumentPropertiesW")]
    public static extern int DocumentProperties(
        IntPtr hWnd,
        IntPtr hPrinter,
        [MarshalAs(UnmanagedType.LPWStr)] string pDeviceName,
        IntPtr pDevModeOutput,
        IntPtr pDevModeInput,
        uint fMode);

    public const uint DM_IN_BUFFER = 0x00000008;
    public const uint DM_OUT_BUFFER = 0x00000002;
    public const uint DM_IN_PROMPT = 0x00000004;
    public const uint DM_PROMPT = 0x00000004;
}
