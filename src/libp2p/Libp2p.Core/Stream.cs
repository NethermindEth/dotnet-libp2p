// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public class ChannelStream(IChannel chan) : Stream
{
    private bool _disposed = false;
    private bool _canRead = true;
    private bool _canWrite = true;

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

        ReadResult result = chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny).Result;
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
        if (chan.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count))).Result != IOResult.Ok)
        {
            _canWrite = false;
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if ((await chan.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count)))) != IOResult.Ok)
        {
            _canWrite = false;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => base.WriteAsync(buffer, cancellationToken);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (buffer is { Length: 0 } && _canRead) return 0;

        ReadResult result = await chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny);
        if (result.Result != IOResult.Ok)
        {
            _canRead = false;
            return 0;
        }

        result.Data.CopyTo(buffer);
        return (int)result.Data.Length;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _ = chan.CloseAsync();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
