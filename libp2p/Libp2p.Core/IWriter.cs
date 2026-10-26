// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Buffers;
using System.Text;
using Nethermind.Channels;

namespace Nethermind.Libp2p.Core;

public interface IWriter
{
    /// <summary>Writes pooled storage while retaining an independent channel lease.</summary>
    /// <remarks>
    /// The caller retains its lease and must not modify published bytes while readers use them.
    /// Writers without ownership support detach a copy for their existing read contract.
    /// </remarks>
    ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        => WriteAsync(new ReadOnlySequence<byte>(buffer.Memory.Slice(offset, length).ToArray()), token);

    /// <summary>Writes a retained slice without consuming the caller's lease.</summary>
    ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        => WriteAsync(new ReadOnlySequence<byte>(slice.ReadOnlyMemory.ToArray()), token);

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
    /// Cancelling before any of the chunk has been read retracts it and reports
    /// <see cref="IOResult.Cancelled"/>. Once a read has taken part of it the remainder is still
    /// owed to the reader, so the call returns only after that remainder is consumed — reporting
    /// <see cref="IOResult.Ok"/>, because the bytes did arrive — or after the channel is closed.
    /// The token does not bound how long that takes.
    /// </remarks>
    ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default);
    ValueTask<IOResult> WriteEofAsync(CancellationToken token = default);
}

