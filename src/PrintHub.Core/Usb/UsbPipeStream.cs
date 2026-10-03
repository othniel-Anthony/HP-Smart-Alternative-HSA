using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static PrintHub.Core.Usb.NativeMethods;

namespace PrintHub.Core.Usb;

/// <summary>A byte stream over a WinUSB interface's bulk IN/OUT endpoints. HTTP is tunnelled through it.</summary>
public sealed class UsbPipeStream : Stream
{
    readonly SafeFileHandle _file;
    IntPtr _iface;
    readonly byte _in, _out;
    readonly byte[] _rx;
    int _rxPos, _rxLen;

    UsbPipeStream(SafeFileHandle file, IntPtr iface, byte pipeIn, byte pipeOut, int inPacket)
    {
        _file = file; _iface = iface; _in = pipeIn; _out = pipeOut;
        // read size must be a multiple of the endpoint's max packet size
        _rx = new byte[Math.Max(inPacket, 1) * Math.Max(1, 16384 / Math.Max(inPacket, 1))];
    }

    public static UsbPipeStream Open(string devicePath, int timeoutMs = 10_000)
    {
        var file = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {devicePath}");
        if (!WinUsb_Initialize(file, out var iface))
        {
            var err = Marshal.GetLastWin32Error(); file.Dispose();
            throw new Win32Exception(err, "WinUsb_Initialize failed");
        }
        try
        {
            if (!WinUsb_QueryInterfaceSettings(iface, 0, out var d)) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte pin = 0, pout = 0; int inMps = 512;
            for (byte i = 0; i < d.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(iface, 0, i, out var p) || p.PipeType != 2) continue;
                if ((p.PipeId & 0x80) != 0) { pin = p.PipeId; inMps = p.MaximumPacketSize; } else pout = p.PipeId;
            }
            if (pin == 0 || pout == 0) throw new InvalidOperationException("Interface has no bulk IN/OUT endpoint pair.");

            uint t = (uint)timeoutMs, on = 1;
            WinUsb_SetPipePolicy(iface, pin, PIPE_TRANSFER_TIMEOUT, 4, ref t);
            WinUsb_SetPipePolicy(iface, pout, PIPE_TRANSFER_TIMEOUT, 4, ref t);
            WinUsb_SetPipePolicy(iface, pout, SHORT_PACKET_TERMINATE, 1, ref on); // sends the zero-length packet IPP-USB expects
            WinUsb_SetPipePolicy(iface, pin, AUTO_CLEAR_STALL, 1, ref on);
            WinUsb_FlushPipe(iface, pin);
            return new UsbPipeStream(file, iface, pin, pout, inMps);
        }
        catch { WinUsb_Free(iface); file.Dispose(); throw; }
    }

    /// <summary>Discard bytes the device queued from a previous, aborted exchange.</summary>
    public void Drain(int quietMs = 150)
    {
        uint t = (uint)quietMs, back = 10_000;
        WinUsb_SetPipePolicy(_iface, _in, PIPE_TRANSFER_TIMEOUT, 4, ref t);
        try { while (WinUsb_ReadPipe(_iface, _in, _rx, (uint)_rx.Length, out _, IntPtr.Zero)) { } }
        finally { WinUsb_SetPipePolicy(_iface, _in, PIPE_TRANSFER_TIMEOUT, 4, ref back); _rxPos = _rxLen = 0; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_rxPos == _rxLen)
        {
            if (!WinUsb_ReadPipe(_iface, _in, _rx, (uint)_rx.Length, out var n, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_SEM_TIMEOUT) throw new TimeoutException("USB read timed out");
                throw new Win32Exception(err, "USB read failed");
            }
            _rxPos = 0; _rxLen = (int)n; // a zero-length packet just loops to the next read
        }
        int take = Math.Min(count, _rxLen - _rxPos);
        Buffer.BlockCopy(_rx, _rxPos, buffer, offset, take);
        _rxPos += take;
        return take;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (int pos = 0; pos < count;)
        {
            int n = Math.Min(65536, count - pos);
            var piece = new byte[n];
            Buffer.BlockCopy(buffer, offset + pos, piece, 0, n);
            if (!WinUsb_WritePipe(_iface, _out, piece, (uint)n, out var w, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "USB write failed");
            pos += (int)w;
        }
    }

    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_iface != IntPtr.Zero) { WinUsb_Free(_iface); _iface = IntPtr.Zero; }
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
