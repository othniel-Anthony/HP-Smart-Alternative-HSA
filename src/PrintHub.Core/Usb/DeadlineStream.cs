namespace PrintHub.Core.Usb;

/// <summary>
/// Wraps a printer's USB print port so a read or a write that the port does not end when it is cancelled cannot hang the program.
/// A printer that is busy or in an error state stops taking data, and Windows' USB print driver may then leave the pending call waiting without
/// honouring the cancellation (seen as "stuck in the reset process" or "stuck busy"). The cancellation is given a moment; if the call is
/// still pending after that, the port is closed, which ends every pending call on it, and the caller gets a clear error instead of a wait with no end.
/// </summary>
internal sealed class DeadlineStream : Stream
{
    readonly Stream _inner;
    readonly TimeSpan _grace;
    volatile bool _broken;

    public DeadlineStream(Stream inner, TimeSpan? grace = null) { _inner = inner; _grace = grace ?? TimeSpan.FromSeconds(1.5); }

    /// <summary>True once a call had to be abandoned and the port was closed.</summary>
    public bool Broken => _broken;

    internal static IOException BrokenError() =>
        new("The printer stopped answering on its USB port, so the connection was closed. It is probably busy or in an error state: wait for it to finish, or switch it off and on, then try again.");

    async ValueTask<T> Guard<T>(Func<ValueTask<T>> start, CancellationToken ct)
    {
        if (_broken) throw BrokenError();
        var task = start().AsTask();
        if (task.IsCompleted || !ct.CanBeCanceled) return await task.ConfigureAwait(false);

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult()))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) == task) return await task.ConfigureAwait(false);
        }
        // cancelled: the port should end the call at once
        if (await Task.WhenAny(task, Task.Delay(_grace)).ConfigureAwait(false) == task) return await task.ConfigureAwait(false);

        // it did not: close the port (that ends the pending call) and say so
        _broken = true;
        try { _inner.Dispose(); } catch { }
        _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);   // nobody waits for it any more
        throw BrokenError();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Guard(() => _inner.ReadAsync(buffer, ct), ct);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        await Guard(async () => { await _inner.WriteAsync(buffer, ct).ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);

    public override Task FlushAsync(CancellationToken ct) =>
        Guard(async () => { await _inner.FlushAsync(ct).ConfigureAwait(false); return 0; }, ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => _broken ? throw BrokenError() : _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) { if (_broken) throw BrokenError(); _inner.Write(buffer, offset, count); }
    public override void Flush() { if (!_broken) _inner.Flush(); }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _inner.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
