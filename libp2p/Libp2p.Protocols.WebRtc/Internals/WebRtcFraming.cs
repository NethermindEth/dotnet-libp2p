// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// Length-prefixed framing of webrtc.pb.Message, used on every data channel including the
/// Noise handshake channel: "Encoded messages including their length prefix MUST NOT exceed
/// 16kiB to support all major browsers."
/// </summary>
internal static class WebRtcFraming
{
    public const int MaxFrameSize = 16384;

    /// <summary>
    /// Unsigned-varint prefixes are capped at 9 bytes (63 bits) by the multiformats spec the
    /// framing rule defers to.
    /// </summary>
    private const int MaxVarIntBytes = 9;

    public static byte[] Encode(Message.Types.Flag? flag, ReadOnlyMemory<byte>? payload)
    {
        if (payload.HasValue)
        {
            int maxPayload = MaxPayloadSize(flag.HasValue);
            if (payload.Value.Length > maxPayload)
            {
                throw new ArgumentOutOfRangeException(nameof(payload),
                    $"Payload of {payload.Value.Length} bytes exceeds the {maxPayload} bytes that fit a {MaxFrameSize} byte frame.");
            }
        }

        Message message = new();
        if (flag.HasValue)
        {
            message.Flag = flag.Value;
        }
        if (payload.HasValue)
        {
            message.Message_ = ByteString.CopyFrom(payload.Value.Span);
        }
        return Encode(message);
    }

    public static byte[] Encode(Message message)
    {
        int messageLength = message.CalculateSize();
        int prefixLength = VarInt.GetSizeInBytes(messageLength);
        if (prefixLength + messageLength > MaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(message),
                $"Framed message of {prefixLength + messageLength} bytes exceeds the {MaxFrameSize} byte WebRTC limit.");
        }

        byte[] frame = new byte[prefixLength + messageLength];
        int offset = 0;
        VarInt.Encode(messageLength, frame, ref offset);
        message.WriteTo(frame.AsSpan(offset, messageLength));
        return frame;
    }

    /// <summary>
    /// Largest payload that still fits in a single frame, so callers can chunk against it.
    /// </summary>
    public static int MaxPayloadSize(bool hasFlag)
    {
        int payloadLength = MaxFrameSize - 9;
        while (FrameSize(hasFlag, payloadLength + 1) <= MaxFrameSize)
        {
            payloadLength++;
        }
        return payloadLength;
    }

    /// <summary>
    /// Reads one frame. Returns false when the buffer holds less than a complete frame.
    /// Every malformed input a remote peer can produce surfaces as <see cref="InvalidDataException"/>,
    /// so a receive loop has exactly one exception type to catch.
    /// </summary>
    public static bool TryDecode(Span<byte> buffer, [NotNullWhen(true)] out Message? message, out int consumed)
    {
        message = null;
        consumed = 0;

        // Walk the prefix by hand first: the shared VarInt.Decode cannot distinguish "truncated"
        // from "over-long", and reports the latter as FormatException.
        int prefixLength = 0;
        for (; ; )
        {
            if (prefixLength == buffer.Length)
            {
                return false;
            }

            byte current = buffer[prefixLength++];
            if ((current & 0x80) == 0)
            {
                break;
            }

            if (prefixLength == MaxVarIntBytes)
            {
                throw new InvalidDataException(
                    $"Frame length prefix exceeds the {MaxVarIntBytes} byte unsigned-varint limit.");
            }
        }

        // "The only number that can end in a 0x00 is 0" -- anything else is a padded,
        // non-minimal encoding, which decoders must reject.
        if (prefixLength > 1 && buffer[prefixLength - 1] == 0)
        {
            throw new InvalidDataException("Frame length prefix is not a minimally encoded unsigned varint.");
        }

        int offset = 0;
        ulong messageLength = VarInt.Decode(buffer, ref offset);

        if (messageLength > (ulong)(MaxFrameSize - offset))
        {
            throw new InvalidDataException(
                $"Declared frame of {offset + (long)messageLength} bytes exceeds the {MaxFrameSize} byte WebRTC limit.");
        }

        int frameLength = offset + (int)messageLength;
        if (buffer.Length < frameLength)
        {
            return false;
        }

        try
        {
            message = Message.Parser.ParseFrom(buffer.Slice(offset, (int)messageLength));
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException("Frame body is not a valid webrtc.pb.Message.", ex);
        }

        consumed = frameLength;
        return true;
    }

    private static int FrameSize(bool hasFlag, int payloadLength)
    {
        int messageLength = (hasFlag ? 2 : 0) + 1 + VarInt.GetSizeInBytes(payloadLength) + payloadLength;
        return VarInt.GetSizeInBytes(messageLength) + messageLength;
    }
}
