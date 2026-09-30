// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Libp2p.Protocols.KadDht.Kademlia;

public struct ValueHash256 : IComparable<ValueHash256>, IEquatable<ValueHash256>
{
    public const int HashLength = 32;
    public const int MaxDistance = HashLength * 8;

    private byte[] _bytes;

    public static ValueHash256 Zero => new() { _bytes = new byte[HashLength] };

    public ReadOnlySpan<byte> Bytes => (_bytes ??= new byte[HashLength]).AsSpan();

    public static int CalculateLogDistance(ValueHash256 h1, ValueHash256 h2)
    {
        for (int i = 0; i < HashLength; i++)
        {
            byte xor = (byte)(h1.Bytes[i] ^ h2.Bytes[i]);
            if (xor == 0) continue;

            return MaxDistance - (i * 8) - BitOperations.LeadingZeroCount(xor) + 24;
        }

        return 0;
    }

    public static int Compare(ValueHash256 a, ValueHash256 b, ValueHash256 target)
    {
        ReadOnlySpan<byte> aBytes = a.Bytes;
        ReadOnlySpan<byte> bBytes = b.Bytes;
        ReadOnlySpan<byte> targetBytes = target.Bytes;

        for (int i = 0; i < HashLength; i++)
        {
            int cmp = (aBytes[i] ^ targetBytes[i]).CompareTo(bBytes[i] ^ targetBytes[i]);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    public static ValueHash256 CopyForRandom(ValueHash256 currentHash, ValueHash256 randomizedHash, int matchingPrefixBits)
    {
        if (matchingPrefixBits < 0 || matchingPrefixBits > MaxDistance)
            throw new ArgumentOutOfRangeException(nameof(matchingPrefixBits), matchingPrefixBits, "Prefix length must be in the 0..256 range.");

        if (matchingPrefixBits == MaxDistance)
            return currentHash;

        byte[] result = randomizedHash.Bytes.ToArray();
        ReadOnlySpan<byte> prefix = currentHash.Bytes;

        int fullBytes = matchingPrefixBits / 8;
        prefix[..fullBytes].CopyTo(result);

        int remainingBits = matchingPrefixBits % 8;
        if (remainingBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            result[fullBytes] = (byte)((prefix[fullBytes] & mask) | (result[fullBytes] & ~mask));
        }

        int bitByte = matchingPrefixBits / 8;
        byte bitMask = (byte)(1 << (7 - (matchingPrefixBits % 8)));
        if ((prefix[bitByte] & bitMask) == 0)
            result[bitByte] |= bitMask;
        else
            result[bitByte] &= (byte)~bitMask;

        return FromBytes(result);
    }

    public static ValueHash256 FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return FromBytes(bytes.AsSpan());
    }

    public static ValueHash256 FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != HashLength) throw new ArgumentException("ValueHash256 requires 32 bytes.", nameof(bytes));
        return new ValueHash256 { _bytes = bytes.ToArray() };
    }

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance) =>
        GetRandomHashAtDistance(currentHash, distance, Random.Shared);

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance, Random random)
    {
        if (distance < 0 || distance > MaxDistance)
            throw new ArgumentOutOfRangeException(nameof(distance), distance, "Distance must be in the 0..256 range.");

        if (distance == 0) return currentHash;

        byte[] randomized = new byte[HashLength];
        random.NextBytes(randomized);

        return CopyForRandom(currentHash, FromBytes(randomized), MaxDistance - distance);
    }

    public static ValueHash256 XorDistance(ValueHash256 hash1, ValueHash256 hash2)
    {
        byte[] result = new byte[HashLength];
        for (int i = 0; i < HashLength; i++)
            result[i] = (byte)(hash1.Bytes[i] ^ hash2.Bytes[i]);

        return FromBytes(result);
    }

    public int CompareTo(ValueHash256 other)
    {
        for (int i = 0; i < HashLength; i++)
        {
            int cmp = Bytes[i].CompareTo(other.Bytes[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public bool Equals(ValueHash256 other)
    {
        return Bytes.SequenceEqual(other.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is ValueHash256 other && Equals(other);
    }

    public override int GetHashCode()
    {
        ReadOnlySpan<byte> b = Bytes;
        // Use first 8 bytes for a fast hash (sufficient for 32-byte keys)
        HashCode hc = new();
        hc.AddBytes(b[..Math.Min(8, b.Length)]);
        return hc.ToHashCode();
    }

    public static bool operator ==(ValueHash256 left, ValueHash256 right) => left.Equals(right);
    public static bool operator !=(ValueHash256 left, ValueHash256 right) => !left.Equals(right);

    public override string ToString()
    {
        ReadOnlySpan<byte> b = Bytes;
        // first 6 bytes = 12 hex chars for concise id
        return Convert.ToHexString(b[..Math.Min(6, b.Length)]);
    }
}
