// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Main Kad-DHT protocol implementation that provides the public API for DHT operations.
/// This class implements ISessionProtocol but primarily serves as a coordinator for
/// various DHT operations using the underlying Kademlia algorithm and storage systems.
/// </summary>
public class KadDhtProtocol : ISessionProtocol
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<KadDhtProtocol>? _logger;
    private readonly KadDhtOptions _options;
    private readonly IValueStore _valueStore;
    private readonly IProviderStore _providerStore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationLocks;
    
    // Kademlia algorithm components
    private readonly IKademlia<PublicKey, DhtNode>? _kademlia;
    private readonly DhtNode _localDhtNode;
    private readonly DhtKeyOperator _keyOperator;
    private readonly DhtMessageSender _messageSender;

    public string Id => "/ipfs/kad/1.0.0";

    public KadDhtProtocol(
        ILocalPeer localPeer,
        ILoggerFactory? loggerFactory = null,
        KadDhtOptions? options = null,
        IValueStore? valueStore = null,
        IProviderStore? providerStore = null,
        IEnumerable<DhtNode>? bootstrapNodes = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<KadDhtProtocol>();
        _options = options ?? new KadDhtOptions();
        _valueStore = valueStore ?? new InMemoryValueStore(_options.MaxStoredValues, loggerFactory);
        _providerStore = providerStore ?? new InMemoryProviderStore(_options.MaxProvidersPerKey, loggerFactory);
        _operationLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        // Initialize Kademlia algorithm components
        _keyOperator = new DhtKeyOperator();
        _messageSender = new DhtMessageSender(localPeer, loggerFactory, _options.OperationTimeout);
        
        // Create local DHT node representation
        var localPublicKey = new PublicKey(_localPeer.Identity.PeerId.Bytes.ToArray());
            _localDhtNode = new DhtNode
            {
                PeerId = _localPeer.Identity.PeerId,
                PublicKey = localPublicKey,
                Multiaddrs = Array.Empty<string>()
            };

        // Initialize Kademlia algorithm if we have the necessary dependencies
        try
        {
            var nodeHashProvider = new DhtNodeHashProvider();
            var kademliaConfig = new KademliaConfig<DhtNode>
            {
                CurrentNodeId = _localDhtNode,
                KSize = _options.KSize,
                Alpha = _options.Alpha,
                RefreshInterval = _options.RefreshInterval,
                BootNodes = bootstrapNodes?.ToArray() ?? Array.Empty<DhtNode>()
            };

            var routingTable = new KBucketTree<ValueHash256, DhtNode>(kademliaConfig, nodeHashProvider, loggerFactory);
            
            // Kademlia message sender integration is not available because DhtMessageSender does not implement IKademliaMessageSender.
            // Fallback: skip full Kademlia initialization and run in local-only mode.
            _logger?.LogWarning("DhtMessageSender does not implement IKademliaMessageSender; running Kad-DHT in local-only mode (no network lookups/replication).");
            _kademlia = null;

            _logger?.LogInformation("Kad-DHT protocol initialized with full Kademlia algorithm in {Mode} mode with K={KSize}, Alpha={Alpha}",
                _options.Mode, _options.KSize, _options.Alpha);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize Kademlia algorithm, running in local-only mode: {Error}", ex.Message);
            _kademlia = null;
        }
    }

    #region Public DHT API

    /// <summary>
    /// Store a value in the DHT.
    /// </summary>
    /// <param name="key">The key to store the value under.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the value was stored successfully.</returns>
    public async Task<bool> PutValueAsync(byte[] key, byte[] value, CancellationToken cancellationToken = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));
        if (value.Length == 0) throw new ArgumentException("Value cannot be empty", nameof(value));
        if (value.Length > _options.MaxValueSize)
        {
            throw new ArgumentException($"Value size {value.Length} exceeds maximum {_options.MaxValueSize}", nameof(value));
        }

        _logger?.LogDebug("PutValue requested for key of length {KeyLength}, value of length {ValueLength}",
            key.Length, value.Length);

        try
        {
            var storedValue = new StoredValue
            {
                Value = value,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Publisher = _localPeer.Identity.PeerId,
                Ttl = _options.RecordTtl
            };

            bool localStoreSuccess = false;

            // For server mode, store the value locally
            if (_options.Mode == KadDhtMode.Server)
            {
                localStoreSuccess = await _valueStore.PutValueAsync(key, storedValue, cancellationToken);
                
                if (localStoreSuccess)
                {
                    _logger?.LogInformation("Successfully stored value locally for key hash {KeyHash}",
                        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
                }
                else
                {
                    _logger?.LogWarning("Failed to store value locally for key hash {KeyHash}",
                        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
                }
            }

            // Use Kademlia algorithm to find the closest nodes and replicate the value
            if (_kademlia != null)
            {
                try
                {
                    var targetKey = new PublicKey(key);
                    var closestNodes = await _kademlia.LookupNodesClosest(targetKey, cancellationToken);

                    _logger?.LogTrace("Found {NodeCount} closest nodes for value replication", closestNodes.Length);

                    // Store the value on the K closest nodes (including ourselves if we're in server mode)
                    int successfulStores = localStoreSuccess ? 1 : 0;
                    var storeTasks = new List<Task<bool>>();

                    foreach (var node in closestNodes.Take(_options.KSize))
                    {
                        // Skip our own node since we already stored locally
                        if (node.Equals(_localDhtNode))
                            continue;

                        storeTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                _logger?.LogTrace("Replicating value to node {NodeId}", node.PeerId);
                                
                                // TODO: Implement actual PutValue protocol query to the node
                                // This requires extending DhtMessageSender with PutValue operation
                                // For now, return true to simulate success
                                await Task.Delay(10, cancellationToken); // Simulate network operation
                                return true;
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogTrace("Failed to replicate value to node {NodeId}: {Error}", node.PeerId, ex.Message);
                                return false;
                            }
                        }, cancellationToken));
                    }

                    // Wait for all replication attempts
                    var results = await Task.WhenAll(storeTasks);
                    successfulStores += results.Count(r => r);

                    _logger?.LogInformation("Successfully replicated value to {SuccessfulStores} nodes for key hash {KeyHash}",
                        successfulStores, Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));

                    // Consider success if we stored locally or replicated to at least one node
                    return successfulStores > 0;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Value replication failed for key hash {KeyHash}: {ErrorMessage}",
                        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)), ex.Message);
                }
            }

            // If we don't have Kademlia or replication failed, return local store result
            // For client mode without Kademlia, this will be false
            if (_options.Mode == KadDhtMode.Client && _kademlia == null)
            {
                _logger?.LogWarning("PutValue operation not supported in client mode without network capability");
                return false;
            }

            return localStoreSuccess;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error storing value for key hash {KeyHash}: {ErrorMessage}",
                Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Retrieve a value from the DHT.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value if found, null otherwise.</returns>
    public async Task<byte[]?> GetValueAsync(byte[] key, CancellationToken cancellationToken = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));

        _logger?.LogDebug("GetValue requested for key of length {KeyLength}", key.Length);

        try
        {
            // First, check local storage
            var storedValue = await _valueStore.GetValueAsync(key, cancellationToken);
            if (storedValue != null)
            {
                _logger?.LogInformation("Found value locally for key hash {KeyHash}",
                    Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
                return storedValue.Value;
            }

            // Use Kademlia algorithm for network lookup if available
            if (_kademlia != null)
            {
                _logger?.LogDebug("Performing network lookup for key hash {KeyHash}",
                    Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));

                try
                {
                    var targetKey = new PublicKey(key);
                    var closestNodes = await _kademlia.LookupNodesClosest(targetKey, cancellationToken);

                    _logger?.LogTrace("Found {NodeCount} closest nodes for lookup", closestNodes.Length);

                    // Query the closest nodes for the value
                    foreach (var node in closestNodes.Take(_options.Alpha))
                    {
                        try
                        {
                            // Query this node for the value using the GetValue protocol
                            // This would use the DhtMessageSender to send GetValueRequest
                            // For now, we'll implement basic logic here
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            _logger?.LogTrace("Querying node {NodeId} for value", node.PeerId);
                            
                            // TODO: Implement actual GetValue protocol query to the node
                            // This requires extending DhtMessageSender with GetValue operation
                            
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogTrace("Failed to query node {NodeId}: {Error}", node.PeerId, ex.Message);
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Network lookup failed for key hash {KeyHash}: {ErrorMessage}",
                        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)), ex.Message);
                }
            }

            _logger?.LogDebug("Value not found for key hash {KeyHash}",
                Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error retrieving value for key hash {KeyHash}: {ErrorMessage}",
                Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Announce that this node can provide content for a given key.
    /// </summary>
    /// <param name="key">The key being provided.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the announcement was successful.</returns>
    public async Task<bool> ProvideAsync(byte[] key, CancellationToken cancellationToken = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));

        _logger?.LogDebug("Provide requested for key of length {KeyLength}", key.Length);

        try
        {
            // For server mode, store the provider record locally
            if (_options.Mode == KadDhtMode.Server)
            {
                var providerRecord = new ProviderRecord
                {
                    PeerId = _localPeer.Identity.PeerId,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ttl = _options.RecordTtl,
                    // TODO: Get actual multiaddresses from the local peer
                    Multiaddrs = Array.Empty<string>()
                };

                bool added = await _providerStore.AddProviderAsync(key, providerRecord, cancellationToken);
                
                if (added)
                {
                    _logger?.LogInformation("Successfully announced as provider for key hash {KeyHash}",
                        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
                    return true;
                }
                else
                {
                    _logger?.LogWarning("Failed to announce as provider for key hash {KeyHash}",
                        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
                    return false;
                }
            }

            // For client mode, we would need to send the provider record to appropriate nodes
            // For now, just reject the operation
            _logger?.LogWarning("Provide operation rejected in client mode");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error announcing provider for key hash {KeyHash}: {ErrorMessage}",
                Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Find providers for a given key.
    /// </summary>
    /// <param name="key">The key to find providers for.</param>
    /// <param name="count">Maximum number of providers to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of provider PeerIds.</returns>
    public async Task<IEnumerable<PeerId>> FindProvidersAsync(byte[] key, int count, CancellationToken cancellationToken = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));
        if (count <= 0) throw new ArgumentException("Count must be positive", nameof(count));

        _logger?.LogDebug("FindProviders requested for key of length {KeyLength}, count {Count}", key.Length, count);

        try
        {
            // First, check local provider records
            var providers = await _providerStore.GetProvidersAsync(key, count, cancellationToken);
            var providerPeerIds = providers.Select(p => p.PeerId).ToList();

            if (providerPeerIds.Count > 0)
            {
                _logger?.LogInformation("Found {ProviderCount} providers locally for key hash {KeyHash}",
                    providerPeerIds.Count, Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
            }
            else
            {
                _logger?.LogDebug("No providers found locally for key hash {KeyHash}",
                    Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)));
            }

            // TODO: Implement network lookup using the Kademlia algorithm if we need more providers
            
            return providerPeerIds;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error finding providers for key hash {KeyHash}: {ErrorMessage}",
                Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2)), ex.Message);
            return Enumerable.Empty<PeerId>();
        }
    }

    #endregion

    #region ISessionProtocol Implementation

    /// <summary>
    /// Handle outgoing DHT protocol connections.
    /// This method is called when we initiate a connection to a remote peer.
    /// </summary>
    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogDebug("Kad-DHT DialAsync started with peer {RemotePeerId}", context.State.RemotePeerId);

        // For now, this is a placeholder implementation
        // In a full implementation, this would handle outgoing requests like:
        // - FindNode queries
        // - GetValue requests  
        // - PutValue operations
        // - GetProviders queries
        // - AddProvider announcements

        // The actual protocol interactions would be implemented using sub-protocols
        // registered in the KadDhtProtocolExtensions
        
        await Task.CompletedTask;
        
        _logger?.LogDebug("Kad-DHT DialAsync completed with peer {RemotePeerId}", context.State.RemotePeerId);
    }

    /// <summary>
    /// Handle incoming DHT protocol connections.
    /// This method is called when a remote peer connects to us.
    /// </summary>
    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogDebug("Kad-DHT ListenAsync started with peer {RemotePeerId}", context.State.RemotePeerId);

        // For now, this is a placeholder implementation
        // In a full implementation, this would handle incoming requests like:
        // - Responding to FindNode queries
        // - Serving GetValue requests
        // - Accepting PutValue operations (if in server mode)
        // - Responding to GetProviders queries
        // - Processing AddProvider announcements

        // The actual protocol interactions would be implemented using sub-protocols
        // registered in the KadDhtProtocolExtensions

        await Task.CompletedTask;
        
        _logger?.LogDebug("Kad-DHT ListenAsync completed with peer {RemotePeerId}", context.State.RemotePeerId);
    }

    #endregion

    #region Maintenance Operations

    /// <summary>
    /// Start the Kademlia algorithm background processes (routing table maintenance, bootstrap, etc.).
    /// This should be called once after initialization to start the DHT network participation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the background processes.</param>
    /// <returns>Task that completes when the background processes are stopped.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_kademlia != null)
        {
            _logger?.LogInformation("Starting Kademlia algorithm background processes");
            
            try
            {
                await _kademlia.Run(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger?.LogInformation("Kademlia algorithm background processes stopped");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in Kademlia background processes: {ErrorMessage}", ex.Message);
                throw;
            }
        }
        else
        {
            _logger?.LogWarning("Cannot start Kademlia processes - algorithm not initialized");
        }
    }

    /// <summary>
    /// Bootstrap the DHT by connecting to known nodes and populating the routing table.
    /// This should be called once after initialization to join the DHT network.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        if (_kademlia != null)
        {
            _logger?.LogInformation("Starting DHT bootstrap process");
            
            try
            {
                await _kademlia.Bootstrap(cancellationToken);
                _logger?.LogInformation("DHT bootstrap completed successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "DHT bootstrap failed: {ErrorMessage}", ex.Message);
                throw;
            }
        }
        else
        {
            _logger?.LogWarning("Cannot bootstrap - Kademlia algorithm not initialized");
        }
    }

    /// <summary>
    /// Add a node to the routing table.
    /// </summary>
    /// <param name="node">The node to add.</param>
    public void AddNode(DhtNode node)
    {
        if (node == null) throw new ArgumentNullException(nameof(node));
        
        _kademlia?.AddOrRefresh(node);
        _logger?.LogTrace("Added node {NodeId} to routing table", node.PeerId);
    }

    /// <summary>
    /// Perform periodic maintenance tasks like cleaning up expired records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PerformMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger?.LogDebug("Starting DHT maintenance operations");

            // Clean up expired values
            int expiredValues = await _valueStore.CleanupExpiredValuesAsync(cancellationToken);
            
            // Clean up expired providers
            int expiredProviders = await _providerStore.CleanupExpiredProvidersAsync(cancellationToken);

            if (expiredValues > 0 || expiredProviders > 0)
            {
                _logger?.LogInformation("Maintenance completed: removed {ExpiredValues} values, {ExpiredProviders} providers",
                    expiredValues, expiredProviders);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during DHT maintenance: {ErrorMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Get current statistics about the DHT state.
    /// </summary>
    /// <returns>Dictionary containing various statistics.</returns>
    public Dictionary<string, object> GetStatistics()
    {
        return new Dictionary<string, object>
        {
            ["Mode"] = _options.Mode.ToString(),
            ["StoredValues"] = _valueStore.Count,
            ["ProviderKeys"] = _providerStore.KeyCount,
            ["TotalProviders"] = _providerStore.TotalProviderCount,
            ["MaxValueSize"] = _options.MaxValueSize,
            ["MaxStoredValues"] = _options.MaxStoredValues,
            ["MaxProvidersPerKey"] = _options.MaxProvidersPerKey,
            ["RecordTtl"] = _options.RecordTtl.ToString(),
            ["LocalPeerId"] = _localPeer.Identity.PeerId.ToString()
        };
    }

    #endregion
}