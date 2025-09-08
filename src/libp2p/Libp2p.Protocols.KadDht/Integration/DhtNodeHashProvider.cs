// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Node hash provider for DHT operations using libp2p types.
/// </summary>
public sealed class DhtNodeHashProvider : INodeHashProvider<ValueHash256, DhtNode>
{
    public ValueHash256 GetHash(DhtNode node)
    {
        return node.PublicKey.Hash;
    }
}