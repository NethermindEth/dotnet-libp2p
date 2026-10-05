// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class ManagedNoiseEncryptedChannel : IChannel
{
    private readonly IChannel _inner;
    private readonly ManagedNoiseCipherState _sendCipher;
    private readonly ManagedNoiseCipherState _receiveCipher;
    private byte[]? _plaintext;
    private int _plaintextOffset;

    public ManagedNoiseEncryptedChannel(IChannel inner, ManagedNoiseCipherState sendCipher, ManagedNoiseCipherState receiveCipher)
    {
        _inner = inner;
        _sendCipher = sendCipher;
        _receiveCipher = receiveCipher;
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
                {
                    return decrypted.Value;
                }
            }

            int available = _plaintext!.Length - _plaintextOffset;
            int toRead = length == 0 || blockingMode == ReadBlockingMode.WaitAny
                ? Math.Min(length == 0 ? available : length, available)
                : Math.Min(length, available);

            ReadOnlySequence<byte> data = new(_plaintext, _plaintextOffset, toRead);
            _plaintextOffset += toRead;
            return new ReadResult { Result = IOResult.Ok, Data = data };
        }
        catch (OperationCanceledException)
        {
            return ReadResult.Cancelled;
        }
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        byte[] plaintext = bytes.ToArray();
        byte[] ciphertext = _sendCipher.Encrypt(ReadOnlySpan<byte>.Empty, plaintext);
        byte[] frame = new byte[2 + ciphertext.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)ciphertext.Length);
        ciphertext.CopyTo(frame.AsSpan(2));
        return await _inner.WriteAsync(new ReadOnlySequence<byte>(frame), token);
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _inner.WriteEofAsync(token);
    public ValueTask CloseAsync() => _inner.CloseAsync();

    private async ValueTask<ReadResult?> DecryptNextFrameAsync(CancellationToken token)
    {
        ReadResult lenResult = await _inner.ReadAsync(2, ReadBlockingMode.WaitAll, token);
        if (lenResult.Result != IOResult.Ok)
        {
            return lenResult;
        }

        int frameLen = BinaryPrimitives.ReadUInt16BigEndian(lenResult.Data.ToArray());
        byte[]? ciphertext = await ReadExactBytesAsync(frameLen, token);
        if (ciphertext is null)
        {
            return ReadResult.Ended;
        }

        _plaintext = _receiveCipher.Decrypt(ReadOnlySpan<byte>.Empty, ciphertext);
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
                return null;
            }

            byte[] chunk = result.Data.ToArray();
            chunk.CopyTo(buf, offset);
            offset += chunk.Length;
        }

        return buf;
    }
}
