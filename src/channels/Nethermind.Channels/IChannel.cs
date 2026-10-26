// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Channels;

public interface IChannel : IReader, IWriter
{
    /// <summary>Closes both directions, reporting an abort if unfinished data must be discarded.</summary>
    ValueTask CloseAsync();
    /// <summary>Aborts both directions and discards pending data.</summary>
    ValueTask AbortAsync();
    /// <summary>Stops inbound reads while preserving the outbound direction.</summary>
    ValueTask CloseReadAsync();
    TaskAwaiter GetAwaiter();
}

public interface IReader
{
    ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default);

    /// <summary>Reads chunks whose leases remain valid until the enumerator advances or is disposed.</summary>
    /// <remarks>Use <see cref="ReadResult.ToSlice"/> to retain owned data beyond the current iteration.</remarks>
    async IAsyncEnumerable<ReadResult> ReadAllAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using ReadResult result = await ReadAsync(0, ReadBlockingMode.WaitAny, token).ConfigureAwait(false);
            if (result.Result == IOResult.Ended) yield break;
            if (result.Result != IOResult.Ok) throw new ChannelClosedException(result.Result);
            yield return result;
        }
    }
}

public interface IWriter
{
    /// <summary>Writes stable borrowed storage. Readers may retain it after the write completes.</summary>
    /// <remarks>
    /// Cancellation retracts an untouched publication. Once a reader takes part, the remainder
    /// stays pending until consumed or discarded by teardown; the token no longer bounds that wait.
    /// The same delivery semantics apply to owned writes.
    /// </remarks>
    ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default);

    /// <summary>Writes a retained region without taking ownership of the caller's lease.</summary>
    /// <remarks>The channel retains its own lease before returning. Published bytes must not be mutated while readers retain them.</remarks>
    ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default);

    ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default);

    /// <summary>Writes the slices atomically in order. The caller retains ownership of each slice.</summary>
    ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default);

    ValueTask<IOResult> WriteEofAsync(CancellationToken token = default);
}

public enum IOResult
{
    Ok,
    Ended,
    Cancelled,
    InternalError,
    Aborted,
}

public enum ReadBlockingMode
{
    WaitAll,
    WaitAny,
    DoNotWait,
}
