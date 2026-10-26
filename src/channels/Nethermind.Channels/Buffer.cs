// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Channels;

/// <summary>
/// Owns a pooled buffer whose storage stays rented while any retained slice is alive.
/// </summary>
/// <remarks>
/// Disposing an owner or slice releases only that lease and is idempotent. Memory obtained
/// from a lease must not be accessed after that lease is disposed. Retaining a new slice
/// may race with disposal; accessing memory and disposing the same lease must not race.
/// </remarks>
public sealed class PooledBuffer : IMemoryOwner<byte>
{
    private readonly ArrayPool<byte> _pool;
    private readonly byte[] _buffer;
    private readonly int _length;
    private int _refCount = 1;
    private int _disposed;

    private PooledBuffer(ArrayPool<byte> pool, byte[] buffer, int length)
    {
        _pool = pool;
        _buffer = buffer;
        _length = length;
    }

    public static PooledBuffer Rent(int length, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArrayPool<byte> chosenPool = pool ?? ArrayPool<byte>.Shared;
        return new PooledBuffer(chosenPool, chosenPool.Rent(length), length);
    }

    public int Length => _length;
    public int RefCount => Volatile.Read(ref _refCount);

    public Slice this[Range range]
    {
        get
        {
            (int start, int length) = range.GetOffsetAndLength(_length);
            return LeaseSlice(start, length);
        }
    }

    public Memory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return GetMemory(0, _length);
        }
    }

    public ReadOnlyMemory<byte> ReadOnlyMemory => Memory;
    public Span<byte> Span => Memory.Span;
    public ReadOnlySpan<byte> ReadOnlySpan => Memory.Span;

    // Callers must already own a link. A retained slice can use storage after the root lease ends.
    internal Memory<byte> GetMemory(int offset, int length) => _buffer.AsMemory(offset, length);

    internal void Retain()
    {
        int count = Volatile.Read(ref _refCount);
        while (true)
        {
            ObjectDisposedException.ThrowIf(count <= 0, this);
            int observed = Interlocked.CompareExchange(ref _refCount, checked(count + 1), count);
            if (observed == count)
            {
                return;
            }
            count = observed;
        }
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            _pool.Return(_buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
        }
    }

    internal Slice LeaseSlice(int offset, int length)
    {
        ValidateRange(offset, length, _length);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Retain();
        if (Volatile.Read(ref _disposed) != 0)
        {
            Release();
            throw new ObjectDisposedException(nameof(PooledBuffer));
        }
        return new Slice(this, offset, length);
    }

    private static void ValidateRange(int offset, int length, int available)
    {
        if ((uint)offset > (uint)available)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if ((uint)length > (uint)(available - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }

    public static implicit operator Span<byte>(PooledBuffer buffer) => buffer.Span;
    public static implicit operator ReadOnlySpan<byte>(PooledBuffer buffer) => buffer.ReadOnlySpan;
    public static implicit operator Memory<byte>(PooledBuffer buffer) => buffer.Memory;
    public static implicit operator ReadOnlyMemory<byte>(PooledBuffer buffer) => buffer.ReadOnlyMemory;

    /// <summary>A retained region with its own independently disposable lease.</summary>
    public sealed class Slice : IMemoryOwner<byte>
    {
        private readonly PooledBuffer _owner;
        private readonly int _offset;
        private readonly int _length;
        private int _disposed;

        internal Slice(PooledBuffer owner, int offset, int length)
        {
            _owner = owner;
            _offset = offset;
            _length = length;
        }

        internal PooledBuffer Owner
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _owner;
            }
        }

        public int Length => _length;
        public Memory<byte> Memory => Owner.GetMemory(_offset, _length);
        public ReadOnlyMemory<byte> ReadOnlyMemory => Memory;
        public Span<byte> Span => Memory.Span;
        public ReadOnlySpan<byte> ReadOnlySpan => Memory.Span;

        public Slice this[Range range]
        {
            get
            {
                (int start, int length) = range.GetOffsetAndLength(_length);
                return LeaseSlice(start, length);
            }
        }

        internal Slice LeaseSlice(int offset, int length)
        {
            ValidateRange(offset, length, _length);
            PooledBuffer owner = Owner;
            owner.Retain();
            if (Volatile.Read(ref _disposed) != 0)
            {
                owner.Release();
                throw new ObjectDisposedException(nameof(Slice));
            }
            return new Slice(owner, _offset + offset, length);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release();
            }
        }

        public static implicit operator Span<byte>(Slice slice) => slice.Span;
        public static implicit operator ReadOnlySpan<byte>(Slice slice) => slice.ReadOnlySpan;
        public static implicit operator Memory<byte>(Slice slice) => slice.Memory;
        public static implicit operator ReadOnlyMemory<byte>(Slice slice) => slice.ReadOnlyMemory;
    }
}
