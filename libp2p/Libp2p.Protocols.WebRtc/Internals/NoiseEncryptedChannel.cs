// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Noise;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class NoiseEncryptedChannel : IChannel
{
    private readonly IChannel _inner;
    private readonly Transport _transport;
    private byte[]? _plaintext;
    private int _plaintextOffset;
    private int _aborted;

    internal NoiseEncryptedChannel(IChannel inner, Transport transport)
    {
        _inner = inner;
        _transport = transport;
    }

    public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();

    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
    {
        if (Volatile.Read(ref _aborted) != 0)
            return ReadResult.Aborted;

        try
        {
            if (_plaintext is null || _plaintextOffset >= _plaintext.Length)
            {
                ReadResult? decrypted = await DecryptNextFrameAsync(token);
                if (decrypted is not null)
                    return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : decrypted.Value;
            }

            int available = _plaintext!.Length - _plaintextOffset;
            int toRead = length == 0 ? available : Math.Min(length, available);
            ReadOnlySequence<byte> data = new(_plaintext, _plaintextOffset, toRead);
            _plaintextOffset += toRead;
            return Volatile.Read(ref _aborted) != 0
                ? ReadResult.Aborted
                : new ReadResult { Result = IOResult.Ok, Data = data };
        }
        catch (OperationCanceledException)
        {
            return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Cancelled;
        }
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        if (Volatile.Read(ref _aborted) != 0)
            return IOResult.Aborted;

        byte[] plaintext = bytes.ToArray();
        byte[] frame = new byte[2 + plaintext.Length + 16];
        int written = _transport.WriteMessage(plaintext, frame.AsSpan(2));
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)written);
        return await _inner.WriteAsync(new ReadOnlySequence<byte>(frame, 0, 2 + written), token);
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) =>
        Volatile.Read(ref _aborted) != 0 ? ValueTask.FromResult(IOResult.Aborted) : _inner.WriteEofAsync(token);
    public ValueTask CloseAsync() => _inner.CloseAsync();
    public ValueTask AbortAsync()
    {
        Interlocked.Exchange(ref _aborted, 1);
        return _inner.AbortAsync();
    }

    private async ValueTask<ReadResult?> DecryptNextFrameAsync(CancellationToken token)
    {
        ReadResult lenResult = await _inner.ReadAsync(2, ReadBlockingMode.WaitAll, token);
        if (lenResult.Result != IOResult.Ok)
            return lenResult;

        int frameLen = BinaryPrimitives.ReadUInt16BigEndian(lenResult.Data.ToArray());
        (byte[]? ciphertext, IOResult result) = await ReadExactBytesAsync(frameLen, token);
        if (result != IOResult.Ok)
            return new ReadResult { Result = result };

        byte[] plainBuf = new byte[Math.Max(0, frameLen - 16)];
        int plainLen = _transport.ReadMessage(ciphertext!, plainBuf);
        _plaintext = plainBuf[..plainLen];
        _plaintextOffset = 0;
        return null;
    }

    private async ValueTask<(byte[]? Bytes, IOResult Result)> ReadExactBytesAsync(int length, CancellationToken token)
    {
        byte[] buf = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            ReadResult result = await _inner.ReadAsync(length - offset, ReadBlockingMode.WaitAll, token);
            if (result.Result != IOResult.Ok)
                return (null, result.Result);
            byte[] chunk = result.Data.ToArray();
            chunk.CopyTo(buf, offset);
            offset += chunk.Length;
        }
        return (buf, IOResult.Ok);
    }
}
