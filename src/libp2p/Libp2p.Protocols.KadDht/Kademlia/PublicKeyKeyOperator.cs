// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.Kademlia;

public class PublicKeyKeyOperator : IKeyOperator<PublicKey, ValueHash256, TestNode>
{
    public PublicKey GetKey(TestNode node) => node.Id;

    public ValueHash256 GetKeyHash(PublicKey key) => key.Hash;

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        Span<byte> randomBytes = stackalloc byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}
