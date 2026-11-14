// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
/// Compares message IDs by value. Collections that use it can also look an ID up by its bytes
/// with <c>GetAlternateLookup&lt;ReadOnlySpan&lt;byte&gt;&gt;</c>, without creating a <see cref="MessageId"/>.
/// </summary>
public sealed class MessageIdComparer : IEqualityComparer<MessageId>, IAlternateEqualityComparer<ReadOnlySpan<byte>, MessageId>
{
    public static MessageIdComparer Instance { get; } = new();

    private MessageIdComparer()
    {
    }

    public bool Equals(MessageId x, MessageId y) => x.Equals(y);

    public int GetHashCode(MessageId obj) => obj.GetHashCode();

    public bool Equals(ReadOnlySpan<byte> alternate, MessageId other) => alternate.SequenceEqual(other.Bytes);

    public int GetHashCode(ReadOnlySpan<byte> alternate) => MessageId.ComputeHashCode(alternate);

    public MessageId Create(ReadOnlySpan<byte> alternate) => new(alternate.ToArray());
}
