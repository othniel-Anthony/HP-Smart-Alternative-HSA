using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PrintHub.Core.Usb;

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;

    public const uint PIPE_TRANSFER_TIMEOUT = 0x03, SHORT_PACKET_TERMINATE = 0x01, AUTO_CLEAR_STALL = 0x02;
    public const int ERROR_SEM_TIMEOUT = 121;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UsbInterfaceDescriptor
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
            bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbPipeInformation
    {
        public int PipeType; // 0 control, 1 iso, 2 bulk, 3 interrupt
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr iface);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_Free(IntPtr iface);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte alt, out UsbInterfaceDescriptor desc);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_QueryPipe(IntPtr iface, byte alt, byte index, out WinUsbPipeInformation info);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_SetPipePolicy(IntPtr iface, byte pipeId, uint policy, uint len, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_FlushPipe(IntPtr iface, byte pipeId);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipeId, byte[] buf, uint len, out uint read, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_WritePipe(IntPtr iface, byte pipeId, byte[] buf, uint len, out uint written, IntPtr ov);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid iface, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid iface, string? deviceId, char[] buffer, uint len, uint flags);
}
