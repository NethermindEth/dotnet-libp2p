// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public class ChannelStream : Stream
{
    private readonly IChannel _chan;
    private readonly bool _leaveOpen;
    private bool _disposed = false;
    private bool _canRead = true;
    private bool _canWrite = true;

    public ChannelStream(IChannel chan) : this(chan, false)
    {
    }

    /// <summary>Wraps a channel with a stream.</summary>
    /// <param name="chan">The channel to wrap.</param>
    /// <param name="leaveOpen">Whether disposing the stream leaves the channel open.</param>
    public ChannelStream(IChannel chan, bool leaveOpen)
    {
        _chan = chan ?? throw new ArgumentNullException(nameof(chan));
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed && _canRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _canWrite;
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            throw new NotSupportedException();
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            throw new NotSupportedException();
        }
        set
        {
            ThrowIfDisposed();
            throw new NotSupportedException();
        }
    }

    public override void Flush()
    {
        ThrowIfDisposed();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (buffer.IsEmpty) return 0;
        if (!_canRead) return 0;

        ValueTask<ReadResult> read = _chan.ReadOwnedAsync(buffer.Length, ReadBlockingMode.WaitAny);
        // A pending ValueTask source does not block in GetResult; convert it once to a Task.
        using ReadResult result = read.IsCompleted ? read.GetAwaiter().GetResult() : read.AsTask().GetAwaiter().GetResult();
        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
            if (result.Result != IOResult.Ended)
            {
                throw new IOException($"Channel read failed: {result.Result}.");
            }
            return 0;
        }

        result.Data.CopyTo(buffer);
        return (int)result.Data.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        ReadOnlyMemory<byte> source = buffer.AsMemory(offset, count);
        ThrowIfDisposed();
        if (source.IsEmpty) return;

        ValueTask<IOResult> write = _chan.WriteAsync(new ReadOnlySequence<byte>(source.ToArray()));
        IOResult result = write.IsCompleted ? write.GetAwaiter().GetResult() : write.AsTask().GetAwaiter().GetResult();
        if (result != IOResult.Ok)
        {
            _canWrite = false;
            throw new IOException($"Channel write failed: {result}.");
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        ReadOnlyMemory<byte> source = buffer.AsMemory(offset, count);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (_disposed) return Task.FromException(CreateObjectDisposedException());
        if (source.IsEmpty) return Task.CompletedTask;

        return WriteAsyncCore(source, cancellationToken).AsTask();
    }

    /// <remarks>
    /// Once a read has taken part of this write, the remainder is owed to the reader and
    /// <paramref name="cancellationToken"/> no longer bounds the call: it returns when that
    /// remainder is consumed, and succeeds, because the bytes did arrive. Closing the channel
    /// ends the write without delivering the rest. Disposing the stream closes the channel unless
    /// it was constructed with leaveOpen set to true.
    /// </remarks>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new ValueTask(Task.FromCanceled(cancellationToken));
        if (_disposed) return new ValueTask(Task.FromException(CreateObjectDisposedException()));
        if (buffer.IsEmpty) return ValueTask.CompletedTask;

        return WriteAsyncCore(buffer, cancellationToken);
    }

    private async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        // Channel writes complete when the reader takes the sequence, before it finishes using it.
        // Keep the bytes stable if the stream caller reuses its buffer after this write completes.
        IOResult result = await _chan.WriteAsync(new ReadOnlySequence<byte>(buffer.ToArray()), cancellationToken).ConfigureAwait(false);
        if (result == IOResult.Cancelled)
        {
            throw CreateOperationCanceledException(cancellationToken);
        }

        if (result != IOResult.Ok)
        {
            _canWrite = false;
            throw new IOException($"Channel write failed: {result}.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Memory<byte> target = buffer.AsMemory(offset, count);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
        if (_disposed) return Task.FromException<int>(CreateObjectDisposedException());
        if (target.IsEmpty)
        {
            return Task.FromResult(0);
        }

        return ReadAsyncCore(target, cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<int>(Task.FromCanceled<int>(cancellationToken));
        }

        if (_disposed)
        {
            return new ValueTask<int>(Task.FromException<int>(CreateObjectDisposedException()));
        }

        if (buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }

        return ReadAsyncCore(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_canRead) return 0;

        using ReadResult result = await _chan.ReadOwnedAsync(buffer.Length, ReadBlockingMode.WaitAny, cancellationToken).ConfigureAwait(false);
        if (result.Result == IOResult.Cancelled)
        {
            throw CreateOperationCanceledException(cancellationToken);
        }

        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
            if (result.Result != IOResult.Ended)
            {
                throw new IOException($"Channel read failed: {result.Result}.");
            }
            return 0;
        }

        result.Data.CopyTo(buffer.Span);
        return (int)result.Data.Length;
    }

    private static OperationCanceledException CreateOperationCanceledException(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : new OperationCanceledException();

    private static ObjectDisposedException CreateObjectDisposedException()
        => new(nameof(ChannelStream));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw CreateObjectDisposedException();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing && !_leaveOpen)
            {
                _ = _chan.CloseAsync();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
