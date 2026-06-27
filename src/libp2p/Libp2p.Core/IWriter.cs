// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IWriter
{
    ChannelBufferHints BufferHints => default;

    PooledBuffer.Slice RentWriteBuffer(int length)
    {
        ChannelBufferHints hints = BufferHints;
        return PooledBuffer.RentSlice(length, hints.PreferredWriteHeadroom, hints.PreferredWriteTailroom);
    }

    async ValueTask<IOResult> WriteLineAsync(string str, bool prependedWithSize = true)
    {
        int len = Encoding.UTF8.GetByteCount(str) + 1;
        int total = VarInt.GetSizeInBytes(len) + len;
        using PooledBuffer.Slice buf = RentWriteBuffer(total);
        int offset = 0;
        VarInt.Encode(len, buf.Span, ref offset);
        Encoding.UTF8.GetBytes(str, buf.Span[offset..]);
        buf.Span[offset + len - 1] = 0x0a;
        return await WriteAsync(buf);
    }

    async ValueTask<IOResult> WriteVarintAsync(int val)
    {
        int size = VarInt.GetSizeInBytes(val);
        using PooledBuffer.Slice buf = RentWriteBuffer(size);
        int offset = 0;
        VarInt.Encode(val, buf.Span, ref offset);
        return await WriteAsync(buf);
    }

    async ValueTask<IOResult> WriteVarintAsync(ulong val)
    {
        int size = VarInt.GetSizeInBytes(val);
        using PooledBuffer.Slice buf = RentWriteBuffer(size);
        int offset = 0;
        VarInt.Encode(val, buf.Span, ref offset);
        return await WriteAsync(buf);
    }

    async ValueTask<IOResult> WriteSizeAndDataAsync(ReadOnlyMemory<byte> data)
    {
        int total = VarInt.GetSizeInBytes(data.Length) + data.Length;
        using PooledBuffer.Slice buf = RentWriteBuffer(total);
        int offset = 0;
        VarInt.Encode(data.Length, buf.Span, ref offset);
        data.Span.CopyTo(buf.Span[offset..]);
        return await WriteAsync(buf);
    }

    async ValueTask WriteSizeAndProtobufAsync<T>(T grpcMessage) where T : IMessage<T>
    {
        int length = grpcMessage.CalculateSize();
        int total = VarInt.GetSizeInBytes(length) + length;
        using PooledBuffer.Slice buf = RentWriteBuffer(total);
        int offset = 0;
        VarInt.Encode(length, buf.Span, ref offset);
        grpcMessage.WriteTo(buf.Span[offset..]);
        await WriteAsync(buf).OrThrow();
    }

    ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default);
    ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default);

    ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        => slice.Length == 0
            ? new ValueTask<IOResult>(IOResult.Ok)
            : WriteAsync(slice.Owner, slice.Length, slice.Offset, token);

    ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, int length, int offset = 0, CancellationToken token = default)
    {
        if ((uint)offset > (uint)slice.Length || (uint)length > (uint)(slice.Length - offset))
        {
            return new ValueTask<IOResult>(IOResult.InternalError);
        }

        return length == 0
            ? new ValueTask<IOResult>(IOResult.Ok)
            : WriteAsync(slice.Owner, length, slice.Offset + offset, token);
    }

    ValueTask<IOResult> WriteAsync(params PooledBuffer.Slice[] slices)
        => WriteAsync((ReadOnlySpan<PooledBuffer.Slice>)slices, default);

    async ValueTask<IOResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        using PooledBuffer buffer = PooledBuffer.Rent(bytes.Length);
        bytes.Span.CopyTo(buffer.Span);
        return await WriteAsync(buffer, bytes.Length, 0, token);
    }

    async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        if (bytes.Length > int.MaxValue)
        {
            return IOResult.InternalError;
        }

        using PooledBuffer buffer = PooledBuffer.Rent((int)bytes.Length);
        bytes.CopyTo(buffer.Span);
        return await WriteAsync(buffer, (int)bytes.Length, 0, token);
    }

    ValueTask<IOResult> WriteEofAsync(CancellationToken token = default);
}
