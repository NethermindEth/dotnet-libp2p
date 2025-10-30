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
        // Create a random key that has the specified prefix at the given depth
        // This is used for bucket refresh operations

        byte[] keyBytes = new byte[32]; // 256 bits
        _rng.GetBytes(keyBytes);

        // Set the prefix bits to match nodePrefix for the specified depth
        byte[] prefixBytes = nodePrefix.Bytes.ToArray();

        // Copy prefix bits up to the specified depth
        int bytesToCopy = Math.Min(depth / 8, Math.Min(keyBytes.Length, prefixBytes.Length));
        Array.Copy(prefixBytes, keyBytes, bytesToCopy);

        // Handle partial byte if depth is not a multiple of 8
        int remainingBits = depth % 8;
        if (remainingBits > 0 && bytesToCopy < keyBytes.Length && bytesToCopy < prefixBytes.Length)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            keyBytes[bytesToCopy] = (byte)((keyBytes[bytesToCopy] & ~mask) | (prefixBytes[bytesToCopy] & mask));
        }

        return new PublicKey(keyBytes);
    }
}
