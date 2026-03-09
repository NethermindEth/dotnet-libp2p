// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Kademlia;

public class PublicKeyKeyOperator : IKeyOperator<PublicKey, ValueHash256, global::Libp2p.Protocols.KadDht.TestNode>
{
    public PublicKey GetKey(global::Libp2p.Protocols.KadDht.TestNode node)
    {
        var peerId = node.Id;
        return TypeAdapters.ToKademliaKey(peerId);
    }

    public ValueHash256 GetKeyHash(PublicKey key) => key.Hash;

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        Span<byte> randomBytes = stackalloc byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}
