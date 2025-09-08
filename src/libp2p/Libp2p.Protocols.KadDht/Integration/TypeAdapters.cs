// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core;
using DtoPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Type adapters to bridge between libp2p Core types and Kademlia algorithm types.
/// </summary>
internal static class TypeAdapters
{
    /// <summary>
    /// Convert PeerId to Kademlia PublicKey.
    /// </summary>
    public static PublicKey ToKademliaKey(this PeerId peerId)
    {
        return new PublicKey(peerId.Bytes);
    }

    /// <summary>
    /// Convert Kademlia PublicKey to PeerId.
    /// </summary>
    public static PeerId ToPeerId(this PublicKey publicKey)
    {
        return new PeerId(publicKey.Bytes.ToArray());
    }

    /// <summary>
    /// Convert PeerId to DhtNode.
    /// </summary>
    public static DhtNode ToDhtNode(this PeerId peerId)
    {
        return new DhtNode
        {
            PeerId = peerId,
            PublicKey = peerId.ToKademliaKey()
        };
    }

    /// <summary>
    /// Convert DhtNode to TestNode for Kademlia operations.
    /// </summary>
    public static TestNode ToTestNode(this DhtNode dhtNode)
    {
        return new TestNode { Id = dhtNode.PublicKey };
    }

    /// <summary>
    /// Convert TestNode to DhtNode.
    /// </summary>
    public static DhtNode ToDhtNode(this TestNode testNode)
    {
        return new DhtNode
        {
            PeerId = testNode.Id.ToPeerId(),
            PublicKey = testNode.Id
        };
    }
}