// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

/// <summary>
/// Array-pool backed buffer with explicit reference counting for channel ownership.
/// </summary>
public sealed class PooledBuffer : IMemoryOwner<byte>
{
    private const int MaxCachedOwners = 1024;
    private static readonly object CachedOwnersLock = new();
    private static readonly PooledBuffer?[] CachedOwners = new PooledBuffer[MaxCachedOwners];
    private static int _cachedOwnerCount;

    private ArrayPool<byte>? _pool;
    private byte[] _buffer = [];
    private int _length;
    private int _refCount;
    private int _returned;

    private PooledBuffer()
    {
    }

    public static PooledBuffer Rent(int length, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        ArrayPool<byte> chosenPool = pool ?? ArrayPool<byte>.Shared;
        PooledBuffer owner = RentOwner();
        owner.Reset(chosenPool, chosenPool.Rent(length), length);
        return owner;
    }

    public static Slice RentSlice(int length, int headroom = 0, int tailroom = 0, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(headroom);
        ArgumentOutOfRangeException.ThrowIfNegative(tailroom);

        int totalLength = checked(length + headroom + tailroom);
        ArrayPool<byte> chosenPool = pool ?? ArrayPool<byte>.Shared;
        PooledBuffer owner = RentOwner();
        owner.Reset(chosenPool, chosenPool.Rent(totalLength), totalLength);
        return new Slice(owner, headroom, length, 0);
    }

    public int Length => _length;
    public int RefCount => Volatile.Read(ref _refCount);
    public byte[] Array => _buffer;
    public Memory<byte> Memory => new(_buffer, 0, _length);
    public ReadOnlyMemory<byte> ReadOnlyMemory => new(_buffer, 0, _length);
    public Span<byte> Span => new(_buffer, 0, _length);
    public ReadOnlySpan<byte> ReadOnlySpan => new(_buffer, 0, _length);

    public Slice this[Range range]
    {
        get
        {
            (int offset, int length) = range.GetOffsetAndLength(_length);
            return LeaseSlice(offset, length, offset);
        }
    }

    internal void Retain()
    {
        int refs = Interlocked.Increment(ref _refCount);
        if (refs <= 1)
        {
            throw new ObjectDisposedException(nameof(PooledBuffer));
        }
    }

    internal void Release()
    {
        int refs = Interlocked.Decrement(ref _refCount);
        if (refs < 0)
        {
            throw new ObjectDisposedException(nameof(PooledBuffer));
        }

        if (refs == 0 && Interlocked.Exchange(ref _returned, 1) == 0)
        {
            ReturnOwner();
        }
    }

    public void Dispose() => Release();

    internal Slice LeaseSlice(int offset, int length, int prependLimit)
    {
        if ((uint)offset > (uint)_length || (uint)length > (uint)(_length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if ((uint)prependLimit > (uint)offset)
        {
            throw new ArgumentOutOfRangeException(nameof(prependLimit));
        }

        Retain();
        return new Slice(this, offset, length, prependLimit);
    }

    public static implicit operator Span<byte>(PooledBuffer buffer) => buffer.Span;
    public static implicit operator ReadOnlySpan<byte>(PooledBuffer buffer) => buffer.ReadOnlySpan;
    public static implicit operator Memory<byte>(PooledBuffer buffer) => buffer.Memory;
    public static implicit operator ReadOnlyMemory<byte>(PooledBuffer buffer) => buffer.ReadOnlyMemory;

    private static PooledBuffer RentOwner()
    {
        lock (CachedOwnersLock)
        {
            int count = _cachedOwnerCount;
            if (count != 0)
            {
                int index = count - 1;
                PooledBuffer owner = CachedOwners[index]!;
                CachedOwners[index] = null;
                _cachedOwnerCount = index;
                return owner;
            }
        }

        return new PooledBuffer();
    }

    private void Reset(ArrayPool<byte> pool, byte[] buffer, int length)
    {
        _pool = pool;
        _buffer = buffer;
        _length = length;
        Volatile.Write(ref _refCount, 1);
        Volatile.Write(ref _returned, 0);
    }

    private void ReturnOwner()
    {
        ArrayPool<byte> pool = _pool ?? throw new ObjectDisposedException(nameof(PooledBuffer));
        byte[] buffer = _buffer;

        _pool = null;
        _buffer = [];
        _length = 0;
        pool.Return(buffer);

        lock (CachedOwnersLock)
        {
            int count = _cachedOwnerCount;
            if (count < MaxCachedOwners)
            {
                CachedOwners[count] = this;
                _cachedOwnerCount = count + 1;
            }
        }
    }

    public struct Slice : IDisposable
    {
        private readonly PooledBuffer? _owner;
        private readonly int _offset;
        private readonly int _length;
        private readonly int _prependLimit;
        private int _released;

        internal Slice(PooledBuffer owner, int offset, int length, int prependLimit)
        {
            _owner = owner;
            _offset = offset;
            _length = length;
            _prependLimit = prependLimit;
            _released = 0;
        }

        internal PooledBuffer Owner => _owner ?? throw new ObjectDisposedException(nameof(Slice));
        internal int Offset => _offset;
        internal int PrependLimit => _prependLimit;

        public int Length => _length;
        public int PrependableLength => _offset - _prependLimit;
        public Span<byte> Span => new(Owner._buffer, _offset, _length);
        public ReadOnlySpan<byte> ReadOnlySpan => new(Owner._buffer, _offset, _length);
        public Memory<byte> Memory => new(Owner._buffer, _offset, _length);
        public ReadOnlyMemory<byte> ReadOnlyMemory => new(Owner._buffer, _offset, _length);

        internal Slice Retain()
        {
            Owner.Retain();
            return new Slice(Owner, _offset, _length, _prependLimit);
        }

        public Slice SliceRange(int offset, int length)
        {
            if ((uint)offset > (uint)_length || (uint)length > (uint)(_length - offset))
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            Owner.Retain();
            int sliceOffset = _offset + offset;
            int prependLimit = offset == 0 ? _prependLimit : sliceOffset;
            return new Slice(Owner, sliceOffset, length, prependLimit);
        }

        public bool TryPrepend(int length, out Slice slice)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            if (length > PrependableLength)
            {
                slice = default;
                return false;
            }

            Owner.Retain();
            slice = new Slice(Owner, _offset - length, _length + length, _prependLimit);
            return true;
        }

        public byte[] ToArray() => ReadOnlySpan.ToArray();

        public void Complete() => Dispose();

        public void Dispose()
        {
            if (_owner is not null && Interlocked.Exchange(ref _released, 1) == 0)
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
