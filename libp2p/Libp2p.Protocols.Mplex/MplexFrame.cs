// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.Mplex.Tests")]

namespace Nethermind.Libp2p.Protocols.Mplex;

internal enum MplexFlag : byte
{
    NewStream,
    MessageReceiver,
    MessageInitiator,
    CloseReceiver,
    CloseInitiator,
    ResetReceiver,
    ResetInitiator,
}

internal readonly record struct MplexFrame(ulong StreamId, MplexFlag Flag, int Length)
{
    internal const ulong MaxStreamId = (1UL << 60) - 1;
    internal const int MaxPayloadLength = 1024 * 1024;

    public static async ValueTask<MplexFrame> ReadAsync(IReader reader, CancellationToken token = default)
    {
        ulong header = await ReadVarintAsync(reader, token);
        ulong length = await ReadVarintAsync(reader, token);
        MplexFlag flag = (MplexFlag)(header & 7);
        Validate(header >> 3, flag, length);
        return new(header >> 3, flag, (int)length);
    }

    public byte[] Encode(ReadOnlySequence<byte> payload)
    {
        Validate(StreamId, Flag, unchecked((ulong)Length));
        if (payload.Length != Length) throw new InvalidDataException("Mplex frame length does not match its payload.");
        ulong header = (StreamId << 3) | (byte)Flag;
        byte[] frame = new byte[VarInt.GetSizeInBytes(header) + VarInt.GetSizeInBytes(Length) + Length];
        int offset = 0;
        VarInt.Encode(header, frame, ref offset);
        VarInt.Encode(Length, frame, ref offset);
        payload.CopyTo(frame.AsSpan(offset));
        return frame;
    }

    private static void Validate(ulong streamId, MplexFlag flag, ulong length)
    {
        if (streamId > MaxStreamId || flag > MplexFlag.ResetInitiator || length > MaxPayloadLength ||
            flag >= MplexFlag.CloseReceiver && length != 0)
            throw new InvalidDataException("Invalid Mplex frame header.");
    }

    private static async ValueTask<ulong> ReadVarintAsync(IReader reader, CancellationToken token)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            token.ThrowIfCancellationRequested();
            byte next = (await reader.ReadAsync(1, token: token).OrThrow()).FirstSpan[0];
            value |= (ulong)(next & 127) << (7 * i);
            if ((next & 128) == 0)
            {
                if (i != 0 && next == 0) throw new InvalidDataException("Non-minimal Mplex varint.");
                return value;
            }
        }

        throw new InvalidDataException("Mplex varint exceeds nine bytes.");
    }
}
