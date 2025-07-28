// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht
{
    /// <summary>
    /// Key operator for PeerId to ValueHash256 conversion.
    /// </summary>
    public class PeerIdKeyOperator : IKeyOperator<PeerId, ValueHash256>
    {
        private readonly ConcurrentDictionary<PeerId, ValueHash256> _hashCache = 
            new ConcurrentDictionary<PeerId, ValueHash256>();

        /// <summary>
        /// Gets the distance between two peers.
        /// </summary>
        /// <param name="a">The first peer.</param>
        /// <param name="b">The second peer.</param>
        /// <returns>The distance between the two peers.</returns>
        public int GetDistance(PeerId a, PeerId b)
        {
            ValueHash256 aHash = GetKey(a);
            ValueHash256 bHash = GetKey(b);
            
            return aHash.GetDistance(bHash);
        }

        /// <summary>
        /// Gets the distance between two keys of type TKey.
        /// </summary>
        /// <typeparam name="TKey">The type of the keys.</typeparam>
        /// <param name="a">The first key.</param>
        /// <param name="b">The second key.</param>
        /// <returns>The distance between the two keys.</returns>
        public int GetDistance<TKey>(TKey a, TKey b)
        {
            if (a is PeerId peerIdA && b is PeerId peerIdB)
            {
                return GetDistance(peerIdA, peerIdB);
            }
            
            throw new ArgumentException($"Unsupported key type: {typeof(TKey)}");
        }

        /// <summary>
        /// Converts a PeerId to a ValueHash256.
        /// </summary>
        /// <param name="peerId">The PeerId to convert.</param>
        /// <returns>A ValueHash256 representation of the PeerId.</returns>
        public ValueHash256 GetKey(PeerId peerId)
        {
            if (peerId == null)
            {
                throw new ArgumentNullException(nameof(peerId));
            }
            
            // Check cache first
            if (_hashCache.TryGetValue(peerId, out var hash))
            {
                return hash;
            }
            
            // Use the peer ID's byte representation directly
            byte[] keyBytes = NormalizeLength(peerId.Bytes, 32);
            
            // Create the ValueHash256 and cache it
            hash = new ValueHash256(keyBytes);
            _hashCache[peerId] = hash;
            
            return hash;
        }

        /// <summary>
        /// Gets a key from bytes for type TKey.
        /// </summary>
        /// <typeparam name="TKey">The type of the key to create.</typeparam>
        /// <param name="bytes">The byte array to convert to a key.</param>
        /// <returns>A key of type TKey.</returns>
        public TKey GetKeyFromBytes<TKey>(byte[] bytes)
        {
            if (typeof(TKey) == typeof(PeerId))
            {
                return (TKey)(object)new PeerId(bytes);
            }
            
            throw new ArgumentException($"Unsupported key type: {typeof(TKey)}");
        }

        /// <summary>
        /// Gets a key representation of the byte array.
        /// </summary>
        /// <param name="key">The byte array to convert to a key.</param>
        /// <returns>A key representation of the byte array.</returns>
        public ValueHash256 GetKeyHash(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            
            // Ensure the key is the right length (32 bytes)
            byte[] normalizedKey = NormalizeLength(key, 32);
            
            return new ValueHash256(normalizedKey);
        }

        /// <summary>
        /// Gets a key representation of a peer.
        /// </summary>
        /// <param name="peerId">The peer to convert to a key.</param>
        /// <returns>A key representation of the peer.</returns>
        public ValueHash256 GetNodeHash(PeerId peerId)
        {
            return GetKey(peerId);
        }

        /// <summary>
        /// Converts a ValueHash256 back to a PeerId.
        /// </summary>
        /// <param name="hash">The ValueHash256 to convert.</param>
        /// <returns>The corresponding PeerId.</returns>
        public PeerId GetPeerId(ValueHash256 hash)
        {
            // First check if we have this hash in our cache
            var cachedPeerId = _hashCache.FirstOrDefault(kvp => kvp.Value.Equals(hash)).Key;
            if (cachedPeerId != null)
            {
                return cachedPeerId;
            }

            // If not found in cache, create a new PeerId from the hash bytes
            return new PeerId(hash.Bytes);
        }

        /// <summary>
        /// Creates a random key at a specific distance from a node prefix.
        /// </summary>
        /// <param name="nodePrefix">The node prefix to use as a base.</param>
        /// <param name="depth">The distance from the node prefix.</param>
        /// <returns>A random key at the specified distance.</returns>
        public byte[] CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
        {
            // Let's use the Hash256XorUtils to do the work
            var randomHash = InternalTable.Kademlia.Hash256XorUtils.GetRandomHashAtDistance(nodePrefix, depth);
            return randomHash.Bytes;
        }
        
        /// <summary>
        /// Ensures a byte array has the specified length.
        /// </summary>
        private static byte[] NormalizeLength(byte[] input, int targetLength)
        {
            if (input.Length == targetLength)
            {
                return input;
            }
            
            var result = new byte[targetLength];
            
            if (input.Length < targetLength)
            {
                // Pad with zeros
                Buffer.BlockCopy(input, 0, result, 0, input.Length);
            }
            else
            {
                // Truncate
                Buffer.BlockCopy(input, 0, result, 0, targetLength);
            }
            
            return result;
        }
    }
} 
