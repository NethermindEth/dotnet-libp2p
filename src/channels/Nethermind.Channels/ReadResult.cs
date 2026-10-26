// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Channels;

/// <summary>A read and its storage lease. Copies share one idempotently disposable lease.</summary>
/// <remarks>Owned memory is valid until disposal. Call <see cref="ToSlice"/> to acquire an independent lease.</remarks>
public readonly struct ReadResult : IDisposable
{
    private readonly ReadOnlySequence<byte> _sequence;
    private readonly PooledBuffer.Slice? _owner;

    private ReadResult(IOResult result, ReadOnlySequence<byte> sequence, PooledBuffer.Slice? owner = null)
    {
        Result = result;
        _sequence = sequence;
        _owner = owner;
    }

    public static readonly ReadResult Ended = new(IOResult.Ended, default);
    public static readonly ReadResult Cancelled = new(IOResult.Cancelled, default);
    public static readonly ReadResult Aborted = new(IOResult.Aborted, default);
    public static readonly ReadResult Empty = new(IOResult.Ok, default);
    public IOResult Result { get; }
    public bool IsOwned => _owner is not null;
    public ReadOnlySequence<byte> Sequence => _owner is null ? _sequence : new(_owner.ReadOnlyMemory);
    public long Length => Sequence.Length;

    /// <summary>The contiguous contents. Use <see cref="Sequence"/> for segmented borrowed data.</summary>
    public ReadOnlyMemory<byte> Memory => Sequence.IsSingleSegment
        ? Sequence.First
        : throw new InvalidOperationException("The read contains multiple segments. Use Sequence.");
    public ReadOnlySpan<byte> Data => Memory.Span;

    internal static ReadResult Ok(ReadOnlySequence<byte> data) => new(IOResult.Ok, data);
    internal static ReadResult Ok(PooledBuffer.Slice owner) => new(IOResult.Ok, default, owner);

    public PooledBuffer.Slice ToSlice() => _owner is not null
        ? _owner.LeaseSlice(0, _owner.Length)
        : throw new InvalidOperationException("A borrowed read has no pooled lease to retain.");

    public byte[] ToArray() => Sequence.ToArray();
    public void Dispose() => _owner?.Dispose();
}
