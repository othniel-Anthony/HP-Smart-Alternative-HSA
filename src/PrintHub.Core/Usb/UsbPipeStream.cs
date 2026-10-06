using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PrintHub.Core.Http;
using static PrintHub.Core.Usb.NativeMethods;

namespace PrintHub.Core.Usb;

/// <summary>A byte stream over a WinUSB interface's bulk IN/OUT endpoints. HTTP is tunnelled through it.</summary>
public sealed class UsbPipeStream : Stream, IReadTimeoutStream
{
    /// <summary>
    /// How long to wait for the printer to start answering. Scanning a page or finishing a print job can legitimately take
    /// a minute or more, so this is generous; <see cref="Abort"/> ends a wait immediately when the connection is closed.
    /// </summary>
    public const int ResponseTimeoutMs = 180_000;

    readonly SafeFileHandle _file;
    IntPtr _iface;
    readonly byte _in, _out;
    readonly byte[] _rx;
    int _rxPos, _rxLen;
    int _timeoutMs;
    volatile bool _aborted;

    UsbPipeStream(SafeFileHandle file, IntPtr iface, byte pipeIn, byte pipeOut, int inPacket, int timeoutMs)
    {
        _file = file; _iface = iface; _in = pipeIn; _out = pipeOut; _timeoutMs = timeoutMs;
        // read size must be a multiple of the endpoint's max packet size
        _rx = new byte[Math.Max(inPacket, 1) * Math.Max(1, 16384 / Math.Max(inPacket, 1))];
    }

    public static UsbPipeStream Open(string devicePath, int timeoutMs = ResponseTimeoutMs)
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
            return new UsbPipeStream(file, iface, pin, pout, inMps, timeoutMs);
        }
        catch { WinUsb_Free(iface); file.Dispose(); throw; }
    }

    /// <summary>Discard bytes the device queued from a previous, aborted exchange.</summary>
    public void Drain(int quietMs = 150)
    {
        uint t = (uint)quietMs, back = (uint)_timeoutMs;
        WinUsb_SetPipePolicy(_iface, _in, PIPE_TRANSFER_TIMEOUT, 4, ref t);
        try
        {
            // some printers (HP's web-services interface) answer an idle read with an instant zero-length packet instead of waiting,
            // so "quiet" is measured on the clock, not by the read failing
            var sw = System.Diagnostics.Stopwatch.StartNew(); long lastData = 0;
            while (sw.ElapsedMilliseconds - lastData < quietMs && sw.ElapsedMilliseconds < 10_000)
            {
                if (!WinUsb_ReadPipe(_iface, _in, _rx, (uint)_rx.Length, out var n, IntPtr.Zero)) break;
                if (n > 0) lastData = sw.ElapsedMilliseconds; else Thread.Sleep(5);
            }
        }
        finally { WinUsb_SetPipePolicy(_iface, _in, PIPE_TRANSFER_TIMEOUT, 4, ref back); _rxPos = _rxLen = 0; }
    }

    /// <summary>Change how long a read waits for the printer before giving up (used for responses without a length).</summary>
    public void SetReadTimeout(int milliseconds)
    {
        if (_iface == IntPtr.Zero) return;
        uint t = (uint)milliseconds;
        WinUsb_SetPipePolicy(_iface, _in, PIPE_TRANSFER_TIMEOUT, 4, ref t);
        _timeoutMs = milliseconds;
    }

    /// <summary>Cancel a read or write that is waiting on the printer, so the connection can be closed right away.</summary>
    public void Abort()
    {
        _aborted = true;
        var iface = _iface;
        if (iface == IntPtr.Zero) return;
        try { WinUsb_AbortPipe(iface, _in); WinUsb_AbortPipe(iface, _out); } catch { }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long deadline = Environment.TickCount64 + _timeoutMs;
        while (_rxPos == _rxLen)
        {
            if (_aborted || _iface == IntPtr.Zero) throw new ObjectDisposedException(nameof(UsbPipeStream));
            if (!WinUsb_ReadPipe(_iface, _in, _rx, (uint)_rx.Length, out var n, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_SEM_TIMEOUT) throw new TimeoutException("USB read timed out");
                if (_aborted) throw new ObjectDisposedException(nameof(UsbPipeStream));
                throw new Win32Exception(err, "USB read failed");
            }
            _rxPos = 0; _rxLen = (int)n;
            if (n == 0)
            {
                // A zero-length packet means "nothing yet": some printers send one at once instead of making the read wait, so the
                // timeout has to be enforced here, and the loop must not spin the CPU while the printer thinks.
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("USB read timed out");
                Thread.Sleep(2);
            }
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
            if (_aborted || _iface == IntPtr.Zero) throw new ObjectDisposedException(nameof(UsbPipeStream));
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
        Abort();
        if (_iface != IntPtr.Zero) { WinUsb_Free(_iface); _iface = IntPtr.Zero; }
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
