// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Channels;

/// <summary>A read and its storage lease. Copies share one idempotently disposable lease.</summary>
/// <remarks>Owned memory is valid until disposal. Call <see cref="ToSlice"/> to acquire an independent lease.</remarks>
public readonly struct ReadResult : IDisposable
{
    private readonly ReadOnlySequence<byte> _sequence;
    internal PooledBuffer.Slice? Owner { get; }

    private ReadResult(IOResult result, ReadOnlySequence<byte> sequence, PooledBuffer.Slice? owner = null)
    {
        Result = result;
        _sequence = sequence;
        Owner = owner;
    }

    public static readonly ReadResult Ended = new(IOResult.Ended, default);
    public static readonly ReadResult Cancelled = new(IOResult.Cancelled, default);
    public static readonly ReadResult Aborted = new(IOResult.Aborted, default);
    public static readonly ReadResult Empty = new(IOResult.Ok, default);
    public IOResult Result { get; }
    public bool IsOwned => Owner is not null;
    public ReadOnlySequence<byte> Sequence => Owner is null ? _sequence : new(Owner.ReadOnlyMemory);
    public long Length => Sequence.Length;

    /// <summary>The contiguous contents. Use <see cref="Sequence"/> for segmented borrowed data.</summary>
    public ReadOnlyMemory<byte> Memory => Sequence.IsSingleSegment
        ? Sequence.First
        : throw new InvalidOperationException("The read contains multiple segments. Use Sequence.");
    public ReadOnlySpan<byte> Data => Memory.Span;

    /// <summary>Creates a successful read over stable borrowed storage that consumers may retain.</summary>
    public static ReadResult Ok(ReadOnlySequence<byte> data) => new(IOResult.Ok, data);

    /// <summary>Transfers the supplied slice lease to a successful read. Disposing the result releases that lease.</summary>
    public static ReadResult FromOwnedSlice(PooledBuffer.Slice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return new(IOResult.Ok, default, slice);
    }

    public PooledBuffer.Slice ToSlice() => Owner is not null
        ? Owner.LeaseSlice(0, Owner.Length)
        : throw new InvalidOperationException("A borrowed read has no pooled lease to retain.");

    /// <summary>Forwards the bytes to a writer or returns this read's failure status.</summary>
    /// <remarks>The writer retains pooled bytes before this method returns. The caller still owns this result.</remarks>
    public ValueTask<IOResult> WriteToAsync(IWriter writer, CancellationToken token = default)
    {
        if (Result != IOResult.Ok) return ValueTask.FromResult(Result);
        ArgumentNullException.ThrowIfNull(writer);
        return Owner is null ? writer.WriteAsync(_sequence, token) : writer.WriteAsync(Owner, token);
    }

    public byte[] ToArray() => Sequence.ToArray();
    public void Dispose() => Owner?.Dispose();
}
