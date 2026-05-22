// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Buffers;

public class ChannelStream : Stream
{
    private readonly IChannel _chan;
    private bool _disposed = false;
    private bool _canRead = true;
    private bool _canWrite = true;

    public ChannelStream(IChannel chan)
    {
        _chan = chan ?? throw new ArgumentNullException(nameof(chan));
    }

    public override bool CanRead => !_disposed && _canRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _canWrite;
    public override long Length => throw new Exception();

    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (buffer.IsEmpty) return 0;

        ReadResult result = _chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny).GetAwaiter().GetResult();
        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
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

        if (_chan.WriteAsync(new ReadOnlySequence<byte>(source)).GetAwaiter().GetResult() != IOResult.Ok)
        {
            _canWrite = false;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        ReadOnlyMemory<byte> source = buffer.AsMemory(offset, count);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (_disposed) return Task.FromException(CreateObjectDisposedException());

        return WriteAsyncCore(source, cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new ValueTask(Task.FromCanceled(cancellationToken));
        if (_disposed) return new ValueTask(Task.FromException(CreateObjectDisposedException()));

        return WriteAsyncCore(buffer, cancellationToken);
    }

    private async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        IOResult result = await _chan.WriteAsync(new ReadOnlySequence<byte>(buffer), cancellationToken);
        if (result == IOResult.Cancelled)
        {
            throw CreateOperationCanceledException(cancellationToken);
        }

        if (result != IOResult.Ok)
        {
            _canWrite = false;
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
        ReadResult result = await _chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny, cancellationToken);
        if (result.Result == IOResult.Cancelled)
        {
            throw CreateOperationCanceledException(cancellationToken);
        }

        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _ = _chan.CloseAsync();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
