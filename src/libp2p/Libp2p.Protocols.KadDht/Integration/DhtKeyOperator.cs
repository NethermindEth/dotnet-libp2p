// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Libp2p.Protocols.KadDht.Kademlia;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Key operator for DHT operations using libp2p types.
/// Implements the IKeyOperator interface for DhtNode and PublicKey types.
/// </summary>
public sealed class DhtKeyOperator : IKeyOperator<PublicKey, ValueHash256, DhtNode>
{
    private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    public PublicKey GetKey(DhtNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.PublicKey;
    }

    public ValueHash256 GetKeyHash(PublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.Hash;
    }

    public ValueHash256 GetNodeHash(DhtNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.PublicKey.Hash;
    }

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        // Bucket refresh: generate a key whose *hash* falls within the target bucket's range.
        // The routing table operates on SHA-256 hashes, so we must set the prefix bits
        // directly on the hash and use PublicKey.FromHash to bypass SHA-256 re-hashing.
        // Setting prefix on raw key bytes and then calling new PublicKey(raw) would go
        // through SHA-256 again, destroying the prefix entirely.

        byte[] hashBytes = new byte[32]; // 256-bit hash
        _rng.GetBytes(hashBytes);

        // Set the prefix bits to match nodePrefix for the specified depth
        byte[] prefixBytes = nodePrefix.Bytes.ToArray();

        int bytesToCopy = Math.Min(depth / 8, Math.Min(hashBytes.Length, prefixBytes.Length));
        Array.Copy(prefixBytes, hashBytes, bytesToCopy);

        // Handle partial byte if depth is not a multiple of 8
        int remainingBits = depth % 8;
        if (remainingBits > 0 && bytesToCopy < hashBytes.Length && bytesToCopy < prefixBytes.Length)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            hashBytes[bytesToCopy] = (byte)((hashBytes[bytesToCopy] & ~mask) | (prefixBytes[bytesToCopy] & mask));
        }

        // Create a PublicKey whose .Hash property returns the crafted hash directly,
        // so routing table lookups target the correct bucket.
        return PublicKey.FromHash(ValueHash256.FromBytes(hashBytes));
    }
}
