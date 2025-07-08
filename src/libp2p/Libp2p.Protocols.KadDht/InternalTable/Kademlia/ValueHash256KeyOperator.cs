using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using System;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class ValueHash256KeyOperator : IKeyOperator<ValueHash256, ValueHash256>
    {
        public int GetDistance(ValueHash256 a, ValueHash256 b)
        {
            return a.GetDistance(b);
        }

        public ValueHash256 GetKey(ValueHash256 node)
        {
            return node;
        }

        public ValueHash256 GetKeyHash(ValueHash256 key)
        {
            return key;
        }

        public ValueHash256 GetNodeHash(ValueHash256 node)
        {
            return node;
        }

        public ValueHash256 CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
        {
            // This is a simplistic implementation - would need to be improved for production

            // Create a random hash
            var random = new Random();
            byte[] result = new byte[32];
            random.NextBytes(result);

            // Ensure it's at the specified distance
            int byteIndex = depth / 8;
            int bitIndex = depth % 8;

            if (byteIndex < 32)
            {
                // Flip the bit at the specified depth
                result[byteIndex] ^= (byte)(1 << bitIndex);

                // Ensure lower bits match for distance
                for (int i = byteIndex + 1; i < 32; i++)
                {
                    result[i] = nodePrefix.Bytes[i];
                }

                // If not the first bit in a byte, ensure higher bits in the byte match too
                if (bitIndex > 0)
                {
                    byte mask = (byte)(0xFF << (bitIndex + 1));
                    result[byteIndex] = (byte)((result[byteIndex] & ~mask) | (nodePrefix.Bytes[byteIndex] & mask));
                }
            }

            return new ValueHash256(result);
        }
    }
}