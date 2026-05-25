// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Protocols.NatTraversal;

public enum HolePunchMessageType
{
    Connect = 100,
    Sync = 300
}

public sealed record HolePunchMessage(HolePunchMessageType Type, IReadOnlyList<Multiaddress> ObservedAddresses)
{
    public static HolePunchMessage Connect(IEnumerable<Multiaddress> observedAddresses)
        => new(HolePunchMessageType.Connect, [.. observedAddresses]);

    public static HolePunchMessage Sync()
        => new(HolePunchMessageType.Sync, []);
}

public static class HolePunchMessageCodec
{
    private const int TypeField = 1;
    private const int ObservedAddressField = 2;

    public static byte[] Encode(HolePunchMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var stream = new MemoryStream();
        WriteTag(stream, TypeField, ProtobufWireType.Varint);
        WriteVarint(stream, (ulong)message.Type);

        foreach (Multiaddress address in message.ObservedAddresses)
        {
            byte[] addressBytes = address.ToBytes();
            WriteTag(stream, ObservedAddressField, ProtobufWireType.LengthDelimited);
            WriteVarint(stream, (ulong)addressBytes.Length);
            stream.Write(addressBytes);
        }

        return stream.ToArray();
    }

    public static HolePunchMessage Decode(ReadOnlySpan<byte> bytes)
    {
        HolePunchMessageType? type = null;
        List<Multiaddress> observedAddresses = [];
        int offset = 0;

        while (offset < bytes.Length)
        {
            ulong tag = ReadVarint(bytes, ref offset);
            int fieldNumber = (int)(tag >> 3);
            ProtobufWireType wireType = (ProtobufWireType)(tag & 0x07);

            switch (fieldNumber, wireType)
            {
                case (TypeField, ProtobufWireType.Varint):
                    type = (HolePunchMessageType)ReadVarint(bytes, ref offset);
                    break;
                case (ObservedAddressField, ProtobufWireType.LengthDelimited):
                    ulong length = ReadVarint(bytes, ref offset);
                    if (length > int.MaxValue || offset + (int)length > bytes.Length)
                        throw new FormatException("Hole punch observed address is truncated.");

                    observedAddresses.Add(Multiaddress.Decode(bytes.Slice(offset, (int)length).ToArray()));
                    offset += (int)length;
                    break;
                default:
                    SkipField(bytes, wireType, ref offset);
                    break;
            }
        }

        return type is null
            ? throw new FormatException("Hole punch message is missing a type.")
            : new HolePunchMessage(type.Value, observedAddresses);
    }

    private static void WriteTag(Stream stream, int fieldNumber, ProtobufWireType wireType)
        => WriteVarint(stream, (ulong)((fieldNumber << 3) | (int)wireType));

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong value = 0;
        int shift = 0;

        while (offset < bytes.Length && shift < 64)
        {
            byte current = bytes[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return value;

            shift += 7;
        }

        throw new FormatException("Invalid protobuf varint.");
    }

    private static void SkipField(ReadOnlySpan<byte> bytes, ProtobufWireType wireType, ref int offset)
    {
        switch (wireType)
        {
            case ProtobufWireType.Varint:
                _ = ReadVarint(bytes, ref offset);
                break;
            case ProtobufWireType.Fixed64:
                offset += 8;
                break;
            case ProtobufWireType.LengthDelimited:
                ulong length = ReadVarint(bytes, ref offset);
                if (length > int.MaxValue)
                    throw new FormatException("Length-delimited protobuf field is too large.");
                offset += (int)length;
                break;
            case ProtobufWireType.Fixed32:
                offset += 4;
                break;
            default:
                throw new FormatException($"Unsupported protobuf wire type {wireType}.");
        }

        if (offset > bytes.Length)
            throw new FormatException("Protobuf field is truncated.");
    }

    private enum ProtobufWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5
    }
}
