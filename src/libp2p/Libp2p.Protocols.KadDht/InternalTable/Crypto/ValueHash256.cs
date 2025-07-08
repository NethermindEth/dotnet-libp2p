// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Libp2p.Protocols.KadDht.InternalTable.Crypto
{
    /// <summary>
    /// Represents a 256-bit hash value used for Kademlia operations.
    /// </summary>
    public readonly struct ValueHash256 : IEquatable<ValueHash256>, IComparable<ValueHash256>
    {
        public static readonly ValueHash256 Zero = new ValueHash256(new byte[32]);
        
        private readonly byte[] _bytes;

        public ValueHash256(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 32)
            {
                throw new ArgumentException("Byte array must be 32 bytes long", nameof(bytes));
            }
            
            _bytes = bytes;
        }

        public ValueHash256()
        {
            _bytes = new byte[32];
        }

        public byte[] Bytes => _bytes.ToArray();

        public Span<byte> BytesAsSpan => _bytes;

        public static ValueHash256 FromBytes(byte[] bytes)
        {
            return new ValueHash256(bytes);
        }

        public static ValueHash256 Random()
        {
            var bytes = new byte[32];
            new Random().NextBytes(bytes);
            return new ValueHash256(bytes);
        }

        public int CompareTo(ValueHash256 other)
        {
            return CompareBytes(_bytes, other._bytes);
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            for (int i = 0; i < 32; i++)
            {
                int comparison = a[i].CompareTo(b[i]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            
            return 0;
        }

        public bool Equals(ValueHash256 other)
        {
            return _bytes.SequenceEqual(other._bytes);
        }

        public override bool Equals(object obj)
        {
            return obj is ValueHash256 other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                foreach (byte b in _bytes)
                {
                    hash = hash * 31 + b;
                }
                return hash;
            }
        }

        public static bool operator ==(ValueHash256 left, ValueHash256 right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ValueHash256 left, ValueHash256 right)
        {
            return !left.Equals(right);
        }

        public static bool operator <(ValueHash256 left, ValueHash256 right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator >(ValueHash256 left, ValueHash256 right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator <=(ValueHash256 left, ValueHash256 right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >=(ValueHash256 left, ValueHash256 right)
        {
            return left.CompareTo(right) >= 0;
        }

        public static ValueHash256 Xor(ValueHash256 a, ValueHash256 b)
        {
            byte[] result = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                result[i] = (byte)(a._bytes[i] ^ b._bytes[i]);
            }
            
            return new ValueHash256(result);
        }

        public int GetDistance(ValueHash256 other)
        {
            ValueHash256 xor = Xor(this, other);
            
            // Find the position of the most significant bit
            for (int i = 0; i < 32; i++)
            {
                if (xor._bytes[i] == 0) continue;
                
                int bitPos = 7;
                byte b = xor._bytes[i];
                
                while ((b & (1 << bitPos)) == 0)
                {
                    bitPos--;
                }
                
                return (31 - i) * 8 + bitPos;
            }
            
            return 0;
        }

        public override string ToString()
        {
            return BitConverter.ToString(_bytes).Replace("-", "").ToLowerInvariant();
        }
    }
} 
