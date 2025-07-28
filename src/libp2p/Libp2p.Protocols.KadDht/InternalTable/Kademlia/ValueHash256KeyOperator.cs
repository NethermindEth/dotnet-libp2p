using Libp2p.Protocols.KadDht.InternalTable.Crypto;
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

        public byte[] CreateRandomKeyAtDistance(ValueHash256 prefix, int distance)
        {
            var random = new Random();
            var bytes = new byte[32];
            random.NextBytes(bytes);
            // Set the prefix bits up to the given distance
            int fullBytes = distance / 8;
            int remainingBits = distance % 8;
            var prefixBytes = prefix.Bytes;
            for (int i = 0; i < fullBytes && i < 32; i++)
                bytes[i] = prefixBytes[i];
            if (fullBytes < 32 && remainingBits > 0)
            {
                byte mask = (byte)(0xFF << (8 - remainingBits));
                bytes[fullBytes] = (byte)((prefixBytes[fullBytes] & mask) | (bytes[fullBytes] & ~mask));
            }
            return bytes;
        }

        public int GetDistance<TKey>(TKey k1, TKey k2)
        {
            throw new NotImplementedException();
        }

        public TKey GetKeyFromBytes<TKey>(byte[] keyBytes)
        {
            throw new NotImplementedException();
        }

        public ValueHash256 GetKeyHash(byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            return new ValueHash256(key);
        }

        public ValueHash256 GetNodeHash(ValueHash256 node)
        {
            // For ValueHash256, the node is the hash itself
            return node;
        }


        /// <summary>
        /// Ensures a byte array has the specified length.
        /// </summary>
        private static byte[] NormalizeLength(byte[] input, int targetLength)
        {
            if (input.Length == targetLength)
            {
                return input;
            }

            var result = new byte[targetLength];

            if (input.Length < targetLength)
            {
                // Pad with zeros
                Buffer.BlockCopy(input, 0, result, 0, input.Length);
            }
            else
            {
                // Truncate
                Buffer.BlockCopy(input, 0, result, 0, targetLength);
            }

            return result;
        }
    }
}
