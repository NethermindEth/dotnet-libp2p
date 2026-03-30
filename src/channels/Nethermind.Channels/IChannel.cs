using System.Runtime.CompilerServices;
using System.Text;

namespace Nethermind.Channels;

public interface IChannel : IReader, IWriter
{
    ValueTask CloseAsync();
    TaskAwaiter GetAwaiter();

    CancellationToken CancellationToken
    {
        get
        {
            CancellationTokenSource cts = new();
            GetAwaiter().OnCompleted(cts.Cancel);
            return cts.Token;
        }
    }
}

public interface IReader
{
    ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default);

    #region Read helpers
    async IAsyncEnumerable<PooledBuffer.Slice> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        for (; ; )
        {
            token.ThrowIfCancellationRequested();

            ReadResult result = await ReadAsync(0, ReadBlockingMode.WaitAny, token);

            switch (result)
            {
                case { Result: IOResult.Ok }:
                    PooledBuffer.Slice lease = result.ToSlice();
                    result.Dispose();
                    yield return lease;
                    break;
                case { Result: IOResult.Ended }:
                    result.Dispose();
                    yield break;
                default:
                    result.Dispose();
                    throw new Exception();
            }
        }
    }

    async Task<string> ReadLineAsync()
    {
        int size = await ReadVarintAsync();
        using ReadResult res = await ReadAsync(size).OrThrow();
        return Encoding.UTF8.GetString(res.Memory.Span).TrimEnd('\n');
    }

    Task<int> ReadVarintAsync(CancellationToken token = default)
    {
        return VarInt.Decode(this, token);
    }

    Task<ulong> ReadVarintUlongAsync()
    {
        return VarInt.DecodeUlong(this);
    }
    #endregion
}


public interface IWriter
{
    ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default);

    ValueTask<IOResult> WriteAsync(params PooledBuffer.Slice[] slices)
    {
        return WriteAsync((ReadOnlySpan<PooledBuffer.Slice>)slices, default);
    }

    ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default);

    ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
    {
        return WriteAsync(new ReadOnlySpan<PooledBuffer.Slice>(new[] { slice }), token);
    }

    async ValueTask<IOResult> WriteLineAsync(string str, bool prependedWithSize = true)
    {
        int len = Encoding.UTF8.GetByteCount(str) + 1;
        int total = VarInt.GetSizeInBytes(len) + len;
        PooledBuffer buf = PooledBuffer.Rent(total);
        int offset = 0;
        VarInt.Encode(len, buf.Span, ref offset);
        Encoding.UTF8.GetBytes(str, buf.Span[offset..]);
        buf.Span[offset + len - 1] = 0x0a;
        try
        {
            return await WriteAsync(buf, total, 0);
        }
        finally
        {
            buf.Dispose();
        }
    }

    async ValueTask<IOResult> WriteVarintAsync(int val)
    {
        int size = VarInt.GetSizeInBytes(val);
        PooledBuffer buf = PooledBuffer.Rent(size);
        int offset = 0;
        VarInt.Encode(val, buf.Span, ref offset);
        try
        {
            return await WriteAsync(buf, size, 0);
        }
        finally
        {
            buf.Dispose();
        }
    }

    async ValueTask<IOResult> WriteVarintAsync(ulong val)
    {
        int size = VarInt.GetSizeInBytes(val);
        PooledBuffer buf = PooledBuffer.Rent(size);
        int offset = 0;
        VarInt.Encode(val, buf.Span, ref offset);
        try
        {
            return await WriteAsync(buf, size, 0);
        }
        finally
        {
            buf.Dispose();
        }
    }

    async ValueTask<IOResult> WriteSizeAndDataAsync(ReadOnlyMemory<byte> data)
    {
        int total = VarInt.GetSizeInBytes(data.Length) + data.Length;
        PooledBuffer buf = PooledBuffer.Rent(total);
        int offset = 0;
        VarInt.Encode(data.Length, buf.Span, ref offset);
        data.Span.CopyTo(buf.Span[offset..]);
        try
        {
            return await WriteAsync(buf, total, 0);
        }
        finally
        {
            buf.Dispose();
        }
    }
    ValueTask<IOResult> WriteEofAsync(CancellationToken token = default);
}

public enum IOResult
{
    Ok,
    Ended,
    Cancelled,
    InternalError,
}

public enum ReadBlockingMode
{
    WaitAll,
    WaitAny,
    DontWait
}


public struct ReadResult : IDisposable
{
    public static ReadResult Ended = new(IOResult.Ended, null, 0, 0);
    public static ReadResult Cancelled = new(IOResult.Cancelled, null, 0, 0);

    public static ReadResult Empty = new(IOResult.Ok, null, 0, 0);
    public IOResult Result { get; }
    public int Length => _length;
    public ReadOnlySpan<byte> Data => _buffer is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_buffer.Array, _offset, _length);
    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(_buffer.Array, _offset, _length);

    private PooledBuffer? _buffer;
    private readonly int _offset;
    private readonly int _length;
    private bool _disposed;

    internal ReadResult(IOResult result, PooledBuffer? buffer, int offset, int length)
    {
        Result = result;
        _buffer = buffer;
        _offset = offset;
        _length = length;
        _disposed = false;
    }

    internal static ReadResult Ok(PooledBuffer buffer, int offset, int length) => new(IOResult.Ok, buffer, offset, length);

    public PooledBuffer.Slice ToSlice()
    {
        if (_buffer is null)
        {
            throw new InvalidOperationException("Cannot lease from an empty read result.");
        }

        return _buffer.LeaseSlice(_offset, _length);
    }

    public byte[] ToArray()
    {
        return _buffer is null ? Array.Empty<byte>() : Memory.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer?.Release();
        _buffer = null;
    }
}

