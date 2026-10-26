// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IWriter
{
    /// <summary>
    /// Receives bytes into channel-owned storage and writes them until the source ends or a write fails.
    /// </summary>
    /// <param name="readAsync">
    /// Fills the supplied memory and returns the number of bytes received, or zero for EOF.
    /// The memory can be smaller than <paramref name="bufferSize"/> and must not be accessed
    /// after the callback completes.
    /// </param>
    /// <param name="bufferSize">The size of each backing buffer, in bytes.</param>
    /// <param name="token">Cancellation for both receiving and writing.</param>
    /// <returns><see cref="IOResult.Ok"/> on source EOF, or the unsuccessful write result.</returns>
    /// <remarks>
    /// Fragments share successive, non-overlapping regions of a buffer. Published bytes are
    /// never reused, even after a write completes: pending reads and returned sequences can
    /// retain them. Storage is reclaimed by the GC when those readers release their references.
    /// This method does not close the writer or write EOF. Source exceptions propagate to the caller.
    /// </remarks>
    async ValueTask<IOResult> WriteFromAsync(
        Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync,
        int bufferSize,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        Memory<byte> buffer = default;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
            {
                buffer = new byte[bufferSize];
            }

            int length = await readAsync(buffer, token).ConfigureAwait(false);
            if ((uint)length > (uint)buffer.Length)
            {
                throw new InvalidOperationException("The source returned a length outside the supplied buffer.");
            }
            if (length == 0)
            {
                return IOResult.Ok;
            }

            IOResult result = await WriteAsync(new ReadOnlySequence<byte>(buffer[..length]), token).ConfigureAwait(false);
            if (result != IOResult.Ok)
            {
                return result;
            }
            buffer = buffer[length..];
        }
    }

    ValueTask<IOResult> WriteLineAsync(string str, bool prependedWithSize = true)
    {
        int len = Encoding.UTF8.GetByteCount(str) + 1;
        byte[] buf = new byte[VarInt.GetSizeInBytes(len) + len];
        int offset = 0;
        VarInt.Encode(len, buf, ref offset);
        Encoding.UTF8.GetBytes(str, 0, str.Length, buf, offset);
        buf[^1] = 0x0a;
        return WriteAsync(new ReadOnlySequence<byte>(buf));
    }

    ValueTask<IOResult> WriteVarintAsync(int val)
    {
        byte[] buf = new byte[VarInt.GetSizeInBytes(val)];
        int offset = 0;
        VarInt.Encode(val, buf, ref offset);
        return WriteAsync(new ReadOnlySequence<byte>(buf));
    }

    ValueTask<IOResult> WriteVarintAsync(ulong val)
    {
        byte[] buf = new byte[VarInt.GetSizeInBytes(val)];
        int offset = 0;
        VarInt.Encode(val, buf, ref offset);
        return WriteAsync(new ReadOnlySequence<byte>(buf));
    }

    ValueTask<IOResult> WriteSizeAndDataAsync(byte[] data)
    {
        byte[] buf = new byte[VarInt.GetSizeInBytes(data.Length) + data.Length];
        int offset = 0;
        VarInt.Encode(data.Length, buf, ref offset);
        Array.ConstrainedCopy(data, 0, buf, offset, data.Length);
        return WriteAsync(new ReadOnlySequence<byte>(buf));
    }

    async ValueTask WriteSizeAndProtobufAsync<T>(T grpcMessage) where T : IMessage<T>
    {
        int length = grpcMessage.CalculateSize();
        byte[] buf = new byte[VarInt.GetSizeInBytes(length) + length];
        int offset = 0;
        VarInt.Encode(length, buf, ref offset);
        grpcMessage.WriteTo(buf.AsSpan(offset));
        await WriteAsync(new ReadOnlySequence<byte>(buf));
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> as one chunk, which is delivered whole or not at all.
    /// </summary>
    /// <remarks>
    /// The reader may retain the supplied storage after this call completes, including while
    /// assembling a longer read. Do not modify or return it to a pool. For reusable receive
    /// sources, use <see cref="WriteFromAsync"/> to manage storage lifetime.
    ///
    /// Cancelling before any of the chunk has been read retracts it and reports
    /// <see cref="IOResult.Cancelled"/>. Once a read has taken part of it the remainder is still
    /// owed to the reader, so the call returns only after that remainder is consumed — reporting
    /// <see cref="IOResult.Ok"/>, because the bytes did arrive — or after the channel is closed.
    /// The token does not bound how long that takes.
    /// </remarks>
    ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default);
    ValueTask<IOResult> WriteEofAsync(CancellationToken token = default);
}

