// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public readonly struct MessageId : IComparable<MessageId>, IComparable, IEquatable<MessageId>
{
    public readonly byte[] Bytes;
    private readonly int hashCode;

    public MessageId(byte[] bytes)
    {
        Bytes = bytes;
        hashCode = bytes is null ? 0 : ComputeHashCode(bytes);
    }

    // Message IDs come from remote peers. The hash is seeded per process, so a peer cannot
    // choose IDs that collide in the router's hash tables.
    private static int ComputeHashCode(ReadOnlySpan<byte> bytes)
    {
        HashCode hash = new();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    public override bool Equals(object? obj) => obj is MessageId other && Equals(other);

    public bool Equals(MessageId other) => ReferenceEquals(Bytes, other.Bytes) || (hashCode == other.hashCode && Bytes.AsSpan().SequenceEqual(other.Bytes));

    public override int GetHashCode() => hashCode;

    public static bool operator ==(MessageId left, MessageId right) => left.Equals(right);

    public static bool operator !=(MessageId left, MessageId right) => !(left == right);

    public int CompareTo(MessageId other) => ReferenceEquals(Bytes, other.Bytes) ? 0 : Bytes.AsSpan().SequenceCompareTo(other.Bytes.AsSpan());

    public int CompareTo(object? obj) => obj is not MessageId other ? 1 : CompareTo(other);

    public override string ToString() => Convert.ToBase64String(Bytes);
}
