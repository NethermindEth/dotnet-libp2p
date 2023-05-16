// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nethermind.Libp2p.Protocols;

[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct YamuxHeader
{
    [FieldOffset(0)] public byte Version;
    [FieldOffset(1)] public YamuxHeaderType Type;
    [FieldOffset(2)] public YamuxHeaderFlags Flags;
    [FieldOffset(4)] public int StreamID;
    [FieldOffset(8)] public int Length;

    public static YamuxHeader FromBytes(Span<byte> data)
    {
        short flags = BinaryPrimitives.ReadInt16BigEndian(data[2..]);
        int streamId = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        int length = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        return new YamuxHeader
        {
            Version = data[0],
            Type = (YamuxHeaderType)data[1],
            Flags = (YamuxHeaderFlags)flags,
            StreamID = streamId,
            Length = length
        };
    }

    public static void ToBytes(Span<byte> data, ref YamuxHeader header)
    {
        data[0] = header.Version;
        data[1] = (byte)header.Type;
        BinaryPrimitives.WriteInt16BigEndian(data[2..], (short)header.Flags);
        BinaryPrimitives.WriteInt32BigEndian(data[4..], header.StreamID);
        BinaryPrimitives.WriteInt32BigEndian(data[8..], header.Length);
    }
}
