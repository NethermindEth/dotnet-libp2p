// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Security.Cryptography;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Kademlia;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Key operator for DHT operations using libp2p types.
/// Implements the IKeyOperator interface for DhtNode and PublicKey types.
/// </summary>
public sealed class DhtKeyOperator : IKeyOperator<PublicKey, DhtNode, ValueHash256>
{
    private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    public PublicKey GetKey(DhtNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.PeerKey;
    }

    public ValueHash256 GetKeyHash(PublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.Hash;
    }

    public ValueHash256 GetNodeHash(DhtNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.PeerHash;
    }

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        // libp2p FIND_NODE carries raw key bytes and receivers derive the DHT key
        // with SHA-256. Creating a raw key whose SHA-256 lands in an arbitrary
        // bucket prefix is not practical, so bucket refresh is approximate random
        // sampling, matching Nethermind discovery's public-key refresh strategy.
        byte[] randomBytes = new byte[32];
        _rng.GetBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}
