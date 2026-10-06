using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PrintHub.Core.Http;
using static PrintHub.Core.Usb.NativeMethods;

namespace PrintHub.Core.Usb;

/// <summary>
/// A byte stream over a USB interface that Windows has given to its scanner driver (usbscan.sys), which is where many HP printers' web-services
/// interface ends up when HP's scan software is installed. The driver exposes the interface as a device file: writing sends on its bulk OUT
/// pipe and reading takes from its bulk IN pipe, so HTTP can be tunnelled through it exactly as through <see cref="UsbPipeStream"/>.
/// </summary>
public sealed class UsbscanStream : Stream, IReadTimeoutStream
{
    /// <summary>The device interface class usbscan registers for the interfaces it drives (GUID_DEVINTERFACE_IMAGE).</summary>
    public static readonly Guid ImageInterface = new("6BDD1FC6-810F-11D0-BEC7-08002BE2092F");

    readonly SafeFileHandle _file;
    readonly FileStream _fs;
    readonly CancellationTokenSource _abort = new();
    volatile int _timeoutMs;
    volatile bool _aborted;

    UsbscanStream(SafeFileHandle file, int timeoutMs)
    {
        _file = file; _timeoutMs = timeoutMs;
        _fs = new FileStream(file, FileAccess.ReadWrite, 4096, isAsync: true);
    }

    public static UsbscanStream Open(string devicePath, int timeoutMs = UsbPipeStream.ResponseTimeoutMs)
    {
        var file = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {devicePath}");
        return new UsbscanStream(file, timeoutMs);
    }

    /// <summary>Discard bytes the printer queued from an earlier, abandoned exchange: reads until nothing arrives for <paramref name="quietMs"/>.</summary>
    public void Drain(int quietMs = 150)
    {
        var buf = new byte[16384];
        var sw = System.Diagnostics.Stopwatch.StartNew(); long lastData = 0;
        while (sw.ElapsedMilliseconds - lastData < quietMs && sw.ElapsedMilliseconds < 10_000 && !_aborted)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
                cts.CancelAfter(Math.Max(20, quietMs));
                int n = _fs.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
                if (n > 0) lastData = sw.ElapsedMilliseconds; else Thread.Sleep(5);
            }
            catch (OperationCanceledException) { break; }   // nothing more was waiting
            catch (IOException) { break; }
        }
    }

    public void SetReadTimeout(int milliseconds) => _timeoutMs = milliseconds;

    /// <summary>Cancel a read or write that is waiting on the printer, so the connection can be closed right away.</summary>
    public void Abort() { _aborted = true; try { _abort.Cancel(); } catch { } }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        long deadline = Environment.TickCount64 + _timeoutMs;
        while (true)
        {
            if (_aborted) throw new ObjectDisposedException(nameof(UsbscanStream));
            long left = deadline - Environment.TickCount64;
            if (left <= 0) throw new TimeoutException("USB read timed out");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
            cts.CancelAfter((int)Math.Min(left, int.MaxValue));
            int n;
            try { n = await _fs.ReadAsync(buffer, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (_aborted) throw new ObjectDisposedException(nameof(UsbscanStream));
                throw new TimeoutException("USB read timed out");
            }
            if (n > 0) return n;
            await Task.Delay(2, ct).ConfigureAwait(false);   // an empty packet means "nothing yet"
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_aborted) throw new ObjectDisposedException(nameof(UsbscanStream));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (int pos = 0; pos < buffer.Length;)
            {
                int n = Math.Min(65536, buffer.Length - pos);
                await _fs.WriteAsync(buffer.Slice(pos, n), cts.Token).ConfigureAwait(false);
                pos += n;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("USB write timed out or was cancelled"); }
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

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
        if (disposing) { try { _fs.Dispose(); } catch { } _file.Dispose(); _abort.Dispose(); }
        base.Dispose(disposing);
    }
}
