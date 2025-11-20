// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Represents a DHT node with libp2p integration.
/// Bridges between Kademlia algorithm types and libp2p types.
/// </summary>
public sealed class DhtNode : IEquatable<DhtNode>, IComparable<DhtNode>
{
    public required PeerId PeerId { get; init; }
    public required PublicKey PublicKey { get; init; }
    public IReadOnlyList<string> Multiaddrs { get; init; } = Array.Empty<string>();

    public DhtNode() { }

    public bool Equals(DhtNode? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return PeerId.Equals(other.PeerId);
    }

    public int CompareTo(DhtNode? other)
    {
        if (other is null) return 1;
        if (ReferenceEquals(this, other)) return 0;
        return string.Compare(PeerId.ToString(), other.PeerId.ToString(), StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is DhtNode other && Equals(other);

    public override int GetHashCode() => PeerId.GetHashCode();

    public override string ToString() => $"DhtNode({PeerId})";

    public static bool operator ==(DhtNode? left, DhtNode? right) => Equals(left, right);
    public static bool operator !=(DhtNode? left, DhtNode? right) => !Equals(left, right);
}
