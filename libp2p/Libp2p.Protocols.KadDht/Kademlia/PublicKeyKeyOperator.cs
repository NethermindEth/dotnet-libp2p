// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Integration;
using Nethermind.Libp2p.Core;
using Nethermind.Kademlia;

namespace Libp2p.Protocols.KadDht.Kademlia;

public class PublicKeyKeyOperator : IKeyOperator<PublicKey, global::Libp2p.Protocols.KadDht.TestNode, ValueHash256>
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
