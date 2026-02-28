// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public struct MessageId(byte[] bytes) : IComparable<MessageId>, IComparable, IEquatable<MessageId>
{
    public readonly byte[] Bytes = bytes;
    private int? hashCode = null;

    public readonly override bool Equals(object? obj) => obj is MessageId other && Equals(other);

    public readonly bool Equals(MessageId other) => ReferenceEquals(Bytes, other.Bytes) || Bytes.SequenceEqual(other.Bytes);

    public override int GetHashCode()
    {
        static int ComputeHash(params byte[] data)
        {
            unchecked
            {
                const int p = 16777619;
                int hash = (int)2166136261;

                for (int i = 0; i < data.Length; i++)
                {
                    hash = (hash ^ data[i]) * p;
                }

                return hash;
            }
        }

        return hashCode ??= ComputeHash(Bytes);
    }

    public static bool operator ==(MessageId left, MessageId right) => left.Equals(right);

    public static bool operator !=(MessageId left, MessageId right) => !(left == right);

    public readonly int CompareTo(MessageId other) => ReferenceEquals(Bytes, other.Bytes) ? 0 : Bytes.AsSpan().SequenceCompareTo(other.Bytes.AsSpan());

    public readonly int CompareTo(object? obj) => obj is not MessageId other ? 1 : CompareTo(other);

    public override readonly string ToString() => Convert.ToBase64String(Bytes);
}
