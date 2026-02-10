// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// High-level DHT client for distributed key-value operations.
/// Implements Kademlia-style PUT/GET with k-peer replication.
/// </summary>
public sealed class DhtClient
{
    private readonly SharedDhtState _sharedState;
    private readonly LibP2pKademliaMessageSender<PublicKey, DhtNode> _messageSender;
    private readonly ILogger<DhtClient> _logger;
    private readonly int _replicationFactor;

    public DhtClient(
        SharedDhtState sharedState,
        LibP2pKademliaMessageSender<PublicKey, DhtNode> messageSender,
        ILoggerFactory? loggerFactory = null,
        int replicationFactor = 3)
    {
        _sharedState = sharedState ?? throw new ArgumentNullException(nameof(sharedState));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _logger = loggerFactory?.CreateLogger<DhtClient>() ?? NullLogger<DhtClient>.Instance;
        _replicationFactor = replicationFactor;
    }

    /// <summary>
    /// Store a value in the DHT with k-peer replication.
    /// Finds the k closest peers to the key and stores the value on them.
    /// </summary>
    public async Task<int> PutValueAsync(string key, string value, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(value);

        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);

        // Calculate key hash for finding closest peers
        var keyHash = SHA256.HashData(keyBytes);
        var targetKey = new PublicKey(keyHash);

        _logger.LogInformation("PUT: Starting distributed storage for key '{Key}'", key);

        // Store locally first
        var publisherBytes = _sharedState.LocalPeerKey?.Bytes.ToArray();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _sharedState.ValueStore.Put(keyBytes, valueBytes, null, timestamp, publisherBytes);
        _logger.LogDebug("PUT: Stored value locally");

        // Find k closest peers to the key from SharedDhtState
        var closestPeers = _sharedState.GetKNearestPeers(targetKey, k: _replicationFactor);

        if (closestPeers.Length == 0)
        {
            _logger.LogWarning("PUT: No peers available for replication, value stored locally only");
            return 1; // Only local storage
        }

        _logger.LogInformation("PUT: Replicating to {Count} closest peers", closestPeers.Length);

        // Store on k closest peers in parallel
        var storeTasks = closestPeers.Select(async peer =>
        {
            try
            {
                var success = await _messageSender.PutValue(
                    peer,
                    keyBytes,
                    valueBytes,
                    signature: null,
                    timestamp: timestamp,
                    publisher: publisherBytes,
                    token: token
                );

                if (success)
                {
                    _logger.LogDebug("PUT: Successfully replicated to peer {PeerId}", peer.PeerId);
                    return 1;
                }
                else
                {
                    _logger.LogWarning("PUT: Failed to replicate to peer {PeerId}", peer.PeerId);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PUT: Error replicating to peer {PeerId}", peer.PeerId);
                return 0;
            }
        });

        var results = await Task.WhenAll(storeTasks);
        var successCount = results.Sum() + 1; // +1 for local storage

        _logger.LogInformation("PUT: Stored value on {Success}/{Total} peers (including local)",
            successCount, closestPeers.Length + 1);

        return successCount;
    }

    /// <summary>
    /// Retrieve a value from the DHT with iterative lookup.
    /// First checks local storage, then queries closest peers until value is found.
    /// </summary>
    public async Task<(bool found, string? value)> GetValueAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);

        _logger.LogInformation("GET: Starting distributed lookup for key '{Key}'", key);

        // Check local storage first
        var localValue = _sharedState.ValueStore.Get(keyBytes);
        if (localValue != null)
        {
            var valueStr = System.Text.Encoding.UTF8.GetString(localValue.Value);
            _logger.LogInformation("GET: Found value in local storage");
            return (true, valueStr);
        }

        // Calculate key hash for finding closest peers
        var keyHash = SHA256.HashData(keyBytes);
        var targetKey = new PublicKey(keyHash);

        // Get closest peers from SharedDhtState
        var closestPeers = _sharedState.GetKNearestPeers(targetKey, k: 16);

        if (closestPeers.Length == 0)
        {
            _logger.LogWarning("GET: No peers available for lookup");
            return (false, null);
        }

        _logger.LogDebug("GET: Querying {Count} closest peers", closestPeers.Length);

        // Query peers in parallel until we find the value
        var queryTasks = closestPeers.Select(async peer =>
        {
            try
            {
                var result = await _messageSender.GetValue(peer, keyBytes, token);
                if (result.found && result.value != null)
                {
                    _logger.LogInformation("GET: Found value on peer {PeerId}", peer.PeerId);

                    // Store locally for caching
                    _sharedState.ValueStore.Put(
                        keyBytes,
                        result.value,
                        result.signature,
                        result.timestamp,
                        result.publisher
                    );

                    return (true, System.Text.Encoding.UTF8.GetString(result.value));
                }
                return (false, (string?)null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GET: Error querying peer {PeerId}", peer.PeerId);
                return (false, (string?)null);
            }
        });

        var results = await Task.WhenAll(queryTasks);

        // Return the first found value
        var foundResult = results.FirstOrDefault(r => r.Item1);
        if (foundResult.Item1)
        {
            _logger.LogInformation("GET: Value found and cached locally");
            return foundResult;
        }

        _logger.LogWarning("GET: Value not found on any peer");
        return (false, null);
    }
}
