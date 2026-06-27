// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public struct ReadResult : IDisposable
{
    public static ReadResult Ended => new(IOResult.Ended, null, 0, 0);
    public static ReadResult Cancelled => new(IOResult.Cancelled, null, 0, 0);
    public static ReadResult Empty => new(IOResult.Ok, null, 0, 0);

    private PooledBuffer? _buffer;
    private readonly int _offset;
    private readonly int _length;
    private readonly int _prependLimit;
    private int _disposed;

    internal ReadResult(IOResult result, PooledBuffer? buffer, int offset, int length, int prependLimit = 0)
    {
        Result = result;
        _buffer = buffer;
        _offset = offset;
        _length = length;
        _prependLimit = prependLimit;
        _disposed = 0;
    }

    public IOResult Result { get; }
    public int Length => _length;
    public ReadOnlySpan<byte> Data => _buffer is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_buffer.Array, _offset, _length);
    public ReadOnlySpan<byte> FirstSpan => Data;
    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(_buffer.Array, _offset, _length);
    public ReadOnlySequence<byte> Sequence => new(Memory);

    public static ReadResult Ok(PooledBuffer buffer, int offset, int length) => new(IOResult.Ok, buffer, offset, length, offset);

    internal static ReadResult Ok(PooledBuffer buffer, int offset, int length, int prependLimit) => new(IOResult.Ok, buffer, offset, length, prependLimit);

    public PooledBuffer.Slice ToSlice()
    {
        if (_buffer is null)
        {
            throw new InvalidOperationException("Cannot create a slice from an empty read result.");
        }

        return _buffer.LeaseSlice(_offset, _length, _prependLimit);
    }

    public byte[] ToArray() => Data.ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _buffer?.Release();
            _buffer = null;
        }
    }
}
