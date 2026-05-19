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

    public override bool CanRead => _canRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _canWrite;
    public override long Length => throw new Exception();

    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer is { Length: 0 } && _canRead) return 0;

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
        if (_chan.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count))).GetAwaiter().GetResult() != IOResult.Ok)
        {
            _canWrite = false;
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if ((await _chan.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count)))) != IOResult.Ok)
        {
            _canWrite = false;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WriteAsyncCore(buffer, cancellationToken);

    private async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if ((await _chan.WriteAsync(new ReadOnlySequence<byte>(buffer), cancellationToken)) != IOResult.Ok)
        {
            _canWrite = false;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0 && _canRead) return 0;

        ReadResult result = await _chan.ReadAsync(count, ReadBlockingMode.WaitAny, cancellationToken);
        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
            return 0;
        }

        result.Data.CopyTo(buffer.AsSpan(offset, count));
        return (int)result.Data.Length;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty && _canRead) return ValueTask.FromResult(0);

        return ReadAsyncCore(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ReadResult result = await _chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny, cancellationToken);
        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
            return 0;
        }

        result.Data.CopyTo(buffer.Span);
        return (int)result.Data.Length;
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
