// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Kademlia;

namespace Libp2p.Protocols.KadDht.Kademlia;

public sealed class ValueHash256Distance : IKademliaDistance<ValueHash256>
{
    public int MaxDistance => ValueHash256.MaxDistance;

    public ValueHash256 Zero => ValueHash256.Zero;

    public int CalculateLogDistance(ValueHash256 left, ValueHash256 right) =>
        ValueHash256.CalculateLogDistance(left, right);

    public int Compare(ValueHash256 left, ValueHash256 right, ValueHash256 target) =>
        ValueHash256.Compare(left, right, target);

    public bool GetBit(ValueHash256 key, int index)
    {
        if ((uint)index >= ValueHash256.MaxDistance)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Bit index must be in the 0..255 range.");

        return (key.Bytes[index / 8] & (1 << (7 - (index % 8)))) != 0;
    }

    public ValueHash256 SetBit(ValueHash256 key, int index)
    {
        if ((uint)index >= ValueHash256.MaxDistance)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Bit index must be in the 0..255 range.");

        byte[] bytes = key.Bytes.ToArray();
        bytes[index / 8] |= (byte)(1 << (7 - (index % 8)));
        return ValueHash256.FromBytes(bytes);
    }
}
