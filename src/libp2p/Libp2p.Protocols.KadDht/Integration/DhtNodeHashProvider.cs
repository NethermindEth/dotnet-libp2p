// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Kademlia;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Node hash provider for DHT operations using libp2p types.
/// </summary>
public sealed class DhtNodeHashProvider : INodeHashProvider<DhtNode, ValueHash256>
{
    public ValueHash256 GetHash(DhtNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.PeerHash;
    }
}
