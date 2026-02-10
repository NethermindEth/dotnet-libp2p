// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Libp2p.Protocols.KadDht.Kademlia;

public struct ValueHash256 : IComparable<ValueHash256>, IEquatable<ValueHash256>, IKademiliaHash<ValueHash256>
{
    private byte[] _bytes;

    public static ValueHash256 Zero => new() { _bytes = new byte[32] };

    public static int MaxDistance => 256;

    public byte[] Bytes
    {
        get => _bytes ??= new byte[32];
        set => _bytes = value;
    }

    public Span<byte> BytesAsSpan => Bytes.AsSpan();

    public static int CalculateLogDistance(ValueHash256 h1, ValueHash256 h2) =>
        Hash256XorUtils.CalculateLogDistance(h1, h2);

    public static int Compare(ValueHash256 a, ValueHash256 b, ValueHash256 c) =>
        Hash256XorUtils.Compare(a, b, c);

    public static ValueHash256 CopyForRandom(ValueHash256 currentHash, ValueHash256 randomizedHash, int distance) =>
        Hash256XorUtils.CopyForRandom(currentHash, randomizedHash, distance);

    public static ValueHash256 FromBytes(byte[] bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("ValueHash256 requires 32 bytes.");
        return new ValueHash256 { _bytes = bytes };
    }

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance) =>
        Hash256XorUtils.GetRandomHashAtDistance(currentHash, distance);

    public static ValueHash256 GetRandomHashAtDistance(ValueHash256 currentHash, int distance, Random random) =>
        Hash256XorUtils.GetRandomHashAtDistance(currentHash, distance, random);

    public static ValueHash256 XorDistance(ValueHash256 hash1, ValueHash256 hash2) =>
        Hash256XorUtils.XorDistance(hash1, hash2);

    public int CompareTo(ValueHash256 other)
    {
        for (int i = 0; i < 32; i++)
        {
            int cmp = Bytes[i].CompareTo(other.Bytes[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public bool Equals(ValueHash256 other)
    {
        return Bytes.AsSpan().SequenceEqual(other.Bytes.AsSpan());
    }

    public override bool Equals(object? obj)
    {
        return obj is ValueHash256 other && Equals(other);
    }

    public override int GetHashCode()
    {
        var b = Bytes;
        // Use first 8 bytes for a fast hash (sufficient for 32-byte keys)
        HashCode hc = new();
        hc.AddBytes(b.AsSpan(0, Math.Min(8, b.Length)));
        return hc.ToHashCode();
    }

    public static bool operator ==(ValueHash256 left, ValueHash256 right) => left.Equals(right);
    public static bool operator !=(ValueHash256 left, ValueHash256 right) => !left.Equals(right);

    public override string ToString()
    {
        var b = Bytes;
        // first 6 bytes = 12 hex chars for concise id
        return Convert.ToHexString(b, 0, Math.Min(6, b.Length));
    }
}
