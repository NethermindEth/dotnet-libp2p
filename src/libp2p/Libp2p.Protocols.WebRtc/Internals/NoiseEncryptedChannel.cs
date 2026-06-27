// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Noise;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class NoiseEncryptedChannel : IChannel
{
    private readonly IChannel _inner;
    private readonly Transport _transport;
    private byte[]? _plaintext;
    private int _plaintextOffset;

    internal NoiseEncryptedChannel(IChannel inner, Transport transport)
    {
        _inner = inner;
        _transport = transport;
    }

    public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();

    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
    {
        try
        {
            if (_plaintext is null || _plaintextOffset >= _plaintext.Length)
            {
                ReadResult? decrypted = await DecryptNextFrameAsync(token);
                if (decrypted is not null)
                    return decrypted.Value;
            }

            int available = _plaintext!.Length - _plaintextOffset;
            int toRead = length == 0 ? available : Math.Min(length, available);
            PooledBuffer data = PooledBuffer.Rent(toRead);
            _plaintext.AsSpan(_plaintextOffset, toRead).CopyTo(data.Span);
            _plaintextOffset += toRead;
            return ReadResult.Ok(data, 0, toRead);
        }
        catch (OperationCanceledException)
        {
            return ReadResult.Cancelled;
        }
    }

    public async ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
    {
        using PooledBuffer frame = PooledBuffer.Rent(2 + length + 16);
        int written = _transport.WriteMessage(buffer.ReadOnlySpan.Slice(offset, length), frame.Span[2..]);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Span, (ushort)written);
        return await _inner.WriteAsync(frame, 2 + written, token: token);
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
    {
        if (slices.Length == 0)
        {
            return ValueTask.FromResult(IOResult.Ok);
        }

        int length = 0;
        for (int i = 0; i < slices.Length; i++)
        {
            length += slices[i].Length;
        }

        PooledBuffer plaintext = PooledBuffer.Rent(length);
        int offset = 0;
        for (int i = 0; i < slices.Length; i++)
        {
            PooledBuffer.Slice slice = slices[i];
            slice.ReadOnlySpan.CopyTo(plaintext.Span[offset..]);
            offset += slice.Length;
        }

        return WriteAndDisposeAsync(plaintext, length, token);
    }

    private async ValueTask<IOResult> WriteAndDisposeAsync(PooledBuffer plaintext, int length, CancellationToken token)
    {
        using (plaintext)
        {
            return await WriteAsync(plaintext, length, token: token);
        }
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _inner.WriteEofAsync(token);
    public ValueTask CloseAsync() => _inner.CloseAsync();

    private async ValueTask<ReadResult?> DecryptNextFrameAsync(CancellationToken token)
    {
        ReadResult lenResult = await _inner.ReadAsync(2, ReadBlockingMode.WaitAll, token);
        if (lenResult.Result != IOResult.Ok)
            return lenResult;

        int frameLen = BinaryPrimitives.ReadUInt16BigEndian(lenResult.Data);
        lenResult.Dispose();
        byte[]? ciphertext = await ReadExactBytesAsync(frameLen, token);
        if (ciphertext is null)
            return ReadResult.Ended;

        byte[] plainBuf = new byte[Math.Max(0, frameLen - 16)];
        int plainLen = _transport.ReadMessage(ciphertext, plainBuf);
        _plaintext = plainBuf[..plainLen];
        _plaintextOffset = 0;
        return null;
    }

    private async ValueTask<byte[]?> ReadExactBytesAsync(int length, CancellationToken token)
    {
        byte[] buf = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            ReadResult result = await _inner.ReadAsync(length - offset, ReadBlockingMode.WaitAll, token);
            if (result.Result != IOResult.Ok)
            {
                result.Dispose();
                return null;
            }
            result.Data.CopyTo(buf.AsSpan(offset));
            offset += result.Length;
            result.Dispose();
        }
        return buf;
    }
}
