// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Channels;

public static class WriterExtensions
{
    /// <summary>Receives into owned pooled storage until the source ends or a channel write fails.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="readAsync">
    /// Fills the supplied memory and returns its byte count, or zero for EOF. Destinations may
    /// be shorter than <paramref name="bufferSize"/>. The callback must stop accessing the
    /// memory when it completes.
    /// </param>
    /// <param name="bufferSize">Maximum receive size and logical size of each rented buffer.</param>
    /// <param name="token">Cancellation for both receiving and writing.</param>
    /// <returns><see cref="IOResult.Ok"/> on source EOF, or the first unsuccessful write result.</returns>
    /// <remarks>
    /// Published regions are reused only after all readers release their leases. Otherwise
    /// receiving continues into unused regions, so fragmented input does not rent a full
    /// buffer for each fragment. Source exceptions propagate; the caller owns EOF and close.
    /// </remarks>
    public static ValueTask<IOResult> WriteFromAsync(this IWriter writer,
        Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync,
        int bufferSize, CancellationToken token = default)
        => WriteFromAsync(writer, readAsync, bufferSize, ArrayPool<byte>.Shared, token);

    internal static ValueTask<IOResult> WriteFromAsync(IWriter writer,
        Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync,
        int bufferSize, ArrayPool<byte> pool, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return WriteFromAsync(writer.WriteAsync, readAsync, bufferSize, pool, token);
    }

    // The libp2p adapter supplies its owned write callback so metrics and custom-writer
    // fallbacks participate in the same pump without another buffer-lifetime implementation.
    internal static async ValueTask<IOResult> WriteFromAsync(
        Func<PooledBuffer, int, int, CancellationToken, ValueTask<IOResult>> writeAsync,
        Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync,
        int bufferSize, ArrayPool<byte> pool, CancellationToken token = default,
        Action<int>? onWrite = null)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        PooledBuffer? buffer = null;
        int offset = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                buffer ??= PooledBuffer.Rent(bufferSize, pool);
                Memory<byte> memory = buffer.Memory[offset..];
                int length = await readAsync(memory, token).ConfigureAwait(false);
                if ((uint)length > (uint)memory.Length)
                {
                    throw new InvalidOperationException("The source returned a length outside the supplied buffer.");
                }
                if (length == 0) return IOResult.Ok;

                IOResult result = await writeAsync(buffer, length, offset, token).ConfigureAwait(false);
                if (result != IOResult.Ok) return result;
                onWrite?.Invoke(length);

                // Acknowledgement happens after the channel acquires any reader leases. Only
                // this pump can publish new regions, so a sole root link makes reuse safe.
                offset = buffer.RefCount == 1 ? 0 : offset + length;
                if (offset == bufferSize)
                {
                    buffer.Dispose();
                    buffer = null;
                    offset = 0;
                }
            }
        }
        finally
        {
            buffer?.Dispose();
        }
    }
}
