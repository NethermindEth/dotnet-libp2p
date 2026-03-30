using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Channels;

/// <summary>
/// Pooled buffer with explicit link counting so ownership can be shared across channels.
/// </summary>
public sealed class PooledBuffer : IMemoryOwner<byte>, IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private readonly byte[] _buffer;
    private readonly int _pooledLength;
    private readonly int _length;
    private int _refCount;
    private int _returned;

    private PooledBuffer(ArrayPool<byte> pool, byte[] buffer, int length)
    {
        _pool = pool;
        _buffer = buffer;
        _pooledLength = buffer.Length;
        _length = length;
        _refCount = 1;
    }

    public static PooledBuffer Rent(int length, ArrayPool<byte>? pool = null)
    {
        ArrayPool<byte> chosenPool = pool ?? ArrayPool<byte>.Shared;
        byte[] buffer = chosenPool.Rent(length);
        return new PooledBuffer(chosenPool, buffer, length);
    }


    private sealed class RawArrayData
    {
        public int Length;
    }

    public int Length => _length;
    public int RefCount => Volatile.Read(ref _refCount);

    public byte[] Array
    {
        get
        {
            Interlocked.CompareExchange(ref Unsafe.As<RawArrayData>(_buffer).Length, _length, _pooledLength);
            return _buffer;
        }
    }

    public Slice this[Range range]
    {
        get
        {
            (int start, int length) = range.GetOffsetAndLength(_length);
            return LeaseSlice(start, length);
        }
    }

    public Memory<byte> Memory => new(_buffer, 0, _length);
    public ReadOnlyMemory<byte> ReadOnlyMemory => new(_buffer, 0, _length);
    public Span<byte> Span => new(_buffer, 0, _length);
    public ReadOnlySpan<byte> ReadOnlySpan => new(_buffer, 0, _length);

    internal void Retain()
    {
        if (Interlocked.Increment(ref _refCount) <= 1)
        {
            throw new ObjectDisposedException(nameof(PooledBuffer));
        }
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            ReturnToPool();
        }
    }

    private void ReturnToPool()
    {
        if (Interlocked.Exchange(ref _returned, 1) != 0)
        {
            return;
        }

        Interlocked.CompareExchange(ref Unsafe.As<RawArrayData>(_buffer).Length, _pooledLength, _length);
        _pool.Return(_buffer);
    }

    public void Dispose() => Release();

    /// <summary>
    /// Adds a link tied to a slice so partial forwarding keeps the buffer alive until the slice is fully consumed.
    /// </summary>
    internal Slice LeaseSlice(int offset, int length)
    {
        if ((uint)offset > (uint)_length || (uint)length > (uint)(_length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Retain();
        return new Slice(this, offset, length);
    }

    public static implicit operator Span<byte>(PooledBuffer buffer) => buffer.Span;
    public static implicit operator ReadOnlySpan<byte>(PooledBuffer buffer) => buffer.ReadOnlySpan;
    public static implicit operator Memory<byte>(PooledBuffer buffer) => buffer.Memory;
    public static implicit operator ReadOnlyMemory<byte>(PooledBuffer buffer) => buffer.ReadOnlyMemory;
    public static implicit operator byte[](PooledBuffer buffer) => buffer.Array;

    /// <summary>
    /// Slice that holds its own link and can be tracked independently as it is forwarded through the pipeline.
    /// </summary>
    public struct Slice : IDisposable
    {
        private readonly PooledBuffer _owner;
        private readonly int _offset;
        private readonly int _length;
        private int _remaining;
        private int _released;

        internal Slice(PooledBuffer owner, int offset, int length)
        {
            _owner = owner;
            _offset = offset;
            _length = length;
            _remaining = length;
            _released = 0;
        }

        internal PooledBuffer Owner => _owner;
        internal int Offset => _offset;

        public int Length => _length;
        public Span<byte> Span => new(_owner._buffer, _offset, _length);
        public ReadOnlySpan<byte> ReadOnlySpan => new(_owner._buffer, _offset, _length);
        public Memory<byte> Memory => new(_owner._buffer, _offset, _length);
        public ReadOnlyMemory<byte> ReadOnlyMemory => new(_owner._buffer, _offset, _length);

        /// <summary>
        /// Marks bytes as consumed; when all bytes are consumed the slice releases its link.
        /// </summary>
        public void Advance(int bytes)
        {
            if (bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            if (_released != 0)
            {
                return;
            }

            _ = Interlocked.Add(ref _remaining, -bytes);
        }

        /// <summary>
        /// Releases the link regardless of remaining bytes.
        /// </summary>
        public void Complete()
        {
            if (_released != 0)
            {
                return;
            }

            ReleaseOnce();
        }

        public void Dispose()
        {
            if (_released != 0)
            {
                return;
            }

            ReleaseOnce();
        }

        private void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _owner.Release();
        }

        public static implicit operator Span<byte>(Slice slice) => slice.Span;
        public static implicit operator ReadOnlySpan<byte>(Slice slice) => slice.ReadOnlySpan;
        public static implicit operator Memory<byte>(Slice slice) => slice.Memory;
        public static implicit operator ReadOnlyMemory<byte>(Slice slice) => slice.ReadOnlyMemory;
    }
}
