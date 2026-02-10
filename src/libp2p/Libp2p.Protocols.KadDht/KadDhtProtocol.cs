// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Google.Protobuf;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

public class KadDhtProtocol : ISessionProtocol
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<KadDhtProtocol>? _logger;
    private readonly KadDhtOptions _options;
    private readonly IValueStore _valueStore;
    private readonly IProviderStore _providerStore;
    private readonly IDhtMessageSender _dhtMessageSender;
    private readonly IRecordValidator _validator;

    private readonly IKademlia<PublicKey, DhtNode>? _kademlia;
    private readonly IRoutingTable<ValueHash256, DhtNode>? _routingTable;
    private readonly ILookupAlgo<ValueHash256, DhtNode>? _lookupAlgo;
    private readonly DhtNode _localDhtNode;
    private readonly DhtKeyOperator _keyOperator;
    private readonly DhtNodeHashProvider _nodeHashProvider;

    private readonly ConcurrentDictionary<string, byte[]> _locallyPublishedValues = new();
    private readonly ConcurrentDictionary<string, byte[]> _locallyProvidedKeys = new();

    public string Id => "/ipfs/kad/1.0.0";

    public IRoutingTable<ValueHash256, DhtNode>? RoutingTable => _routingTable;

    public KadDhtProtocol(
        ILocalPeer localPeer,
        Kademlia.IKademliaMessageSender<PublicKey, DhtNode> messageSender,
        IDhtMessageSender dhtMessageSender,
        KadDhtOptions options,
        IValueStore valueStore,
        IProviderStore providerStore,
        ILoggerFactory? loggerFactory = null,
        IRecordValidator? validator = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<KadDhtProtocol>();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _valueStore = valueStore ?? throw new ArgumentNullException(nameof(valueStore));
        _providerStore = providerStore ?? throw new ArgumentNullException(nameof(providerStore));
        _dhtMessageSender = dhtMessageSender ?? throw new ArgumentNullException(nameof(dhtMessageSender));
        _validator = validator ?? DefaultRecordValidator.Instance;

        _keyOperator = new DhtKeyOperator();
        _nodeHashProvider = new DhtNodeHashProvider();

        var localPublicKey = new PublicKey(_localPeer.Identity.PeerId.Bytes.ToArray());
        _localDhtNode = new DhtNode
        {
            PeerId = _localPeer.Identity.PeerId,
            PublicKey = localPublicKey,
            Multiaddrs = _localPeer.ListenAddresses.Select(addr => addr.ToString()).ToArray()
        };

        try
        {
            var kademliaConfig = new KademliaConfig<DhtNode>
            {
                CurrentNodeId = _localDhtNode,
                KSize = _options.KSize,
                Alpha = _options.Alpha,
                RefreshInterval = _options.RefreshInterval,
                BootNodes = Array.Empty<DhtNode>()
            };

            var effectiveLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _routingTable = new KBucketTree<ValueHash256, DhtNode>(kademliaConfig, _nodeHashProvider, effectiveLoggerFactory);

            var nodeHealthTracker = new NodeHealthTracker<PublicKey, ValueHash256, DhtNode>(kademliaConfig, _routingTable, _nodeHashProvider, messageSender, effectiveLoggerFactory);
            _lookupAlgo = new LookupKNearestNeighbour<ValueHash256, DhtNode>(_routingTable, _nodeHashProvider, nodeHealthTracker, kademliaConfig, effectiveLoggerFactory);

            _kademlia = new Kademlia.Kademlia<PublicKey, ValueHash256, DhtNode>(
                _keyOperator,
                messageSender,
                _routingTable,
                _lookupAlgo,
                effectiveLoggerFactory,
                nodeHealthTracker,
                kademliaConfig);

            _logger?.LogInformation("Kad-DHT initialized in {Mode} mode, K={KSize}, Alpha={Alpha}",
                _options.Mode, _options.KSize, _options.Alpha);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize Kademlia algorithm: {Error}", ex.Message);
            _kademlia = null;
            throw;
        }
    }

    #region Public DHT API

    public async Task<bool> PutValueAsync(byte[] key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));
        if (value.Length == 0) throw new ArgumentException("Value cannot be empty", nameof(value));
        if (value.Length > _options.MaxValueSize)
            throw new ArgumentException($"Value size {value.Length} exceeds maximum {_options.MaxValueSize}", nameof(value));

        if (!_validator.Validate(key, value))
        {
            _logger?.LogWarning("PutValue rejected by validator for key {KeyHash}", KeyHashHex(key));
            return false;
        }

        if (_kademlia is null)
        {
            _logger?.LogWarning("PutValue unavailable — Kademlia not initialized");
            return false;
        }

        var storedValue = new StoredValue
        {
            Value = value,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Publisher = _localPeer.Identity.PeerId,
            Ttl = _options.RecordTtl
        };

        int successCount = 0;

        if (_options.Mode == KadDhtMode.Server)
        {
            if (await _valueStore.PutValueAsync(key, storedValue, cancellationToken))
                successCount++;
        }

        var targetKey = new PublicKey(key);
        var closestNodes = await _kademlia.LookupNodesClosest(targetKey, cancellationToken);

        var tasks = closestNodes
            .Where(n => !n.Equals(_localDhtNode))
            .Take(_options.KSize)
            .Select(async node =>
            {
                try
                {
                    if (await _dhtMessageSender.PutValueAsync(node, key, value, cancellationToken))
                        Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    _logger?.LogTrace("PutValue to {NodeId} failed: {Error}", node.PeerId, ex.Message);
                }
            });

        await Task.WhenAll(tasks);

        _logger?.LogInformation("PutValue replicated to {Count} nodes for key {KeyHash}", successCount, KeyHashHex(key));

        if (successCount > 0)
            _locallyPublishedValues[Convert.ToBase64String(key)] = key;

        return successCount > 0;
    }

    public async Task<byte[]?> GetValueAsync(byte[] key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));

        var localValue = await _valueStore.GetValueAsync(key, cancellationToken);
        if (localValue is { Value.Length: > 0 })
        {
            _logger?.LogDebug("GetValue found locally for key {KeyHash}", KeyHashHex(key));
            return localValue.Value;
        }

        if (_kademlia is null || _lookupAlgo is null)
            return null;

        var collectedValues = new ConcurrentBag<byte[]>();
        var nodesWithStaleValue = new ConcurrentBag<DhtNode>();

        var targetHash = _keyOperator.GetKeyHash(new PublicKey(key));

        await _lookupAlgo.Lookup(
            targetHash,
            _options.KSize,
            async (node, token) =>
            {
                if (node.Equals(_localDhtNode))
                    return _routingTable!.GetKNearestNeighbour(targetHash);

                var result = await _dhtMessageSender.GetValueAsync(node, key, token);
                if (result.HasValue)
                {
                    if (_validator.Validate(key, result.Value))
                        collectedValues.Add(result.Value!);
                    else
                        nodesWithStaleValue.Add(node);
                }

                return result.CloserPeers;
            },
            cancellationToken);

        if (collectedValues.IsEmpty)
        {
            _logger?.LogDebug("GetValue: no value found on network for key {KeyHash}", KeyHashHex(key));
            return null;
        }

        var valuesList = collectedValues.ToList();
        int bestIndex = _validator.Select(key, valuesList);
        if (bestIndex < 0)
            return null;

        var bestValue = valuesList[bestIndex];

        // Entry correction: PUT_VALUE back to nodes that had stale/missing values
        if (nodesWithStaleValue.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var staleNode in nodesWithStaleValue)
                {
                    try { await _dhtMessageSender.PutValueAsync(staleNode, key, bestValue, CancellationToken.None); }
                    catch { /* best-effort correction */ }
                }
            }, CancellationToken.None);
        }

        _logger?.LogInformation("GetValue found {Count} records for key {KeyHash}, selected best", valuesList.Count, KeyHashHex(key));
        return bestValue;
    }

    public async Task<bool> ProvideAsync(byte[] key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));

        if (_kademlia is null)
        {
            _logger?.LogWarning("Provide unavailable — Kademlia not initialized");
            return false;
        }

        int successCount = 0;

        if (_options.Mode == KadDhtMode.Server)
        {
            var record = new ProviderRecord
            {
                PeerId = _localPeer.Identity.PeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = _options.RecordTtl,
                Multiaddrs = _localPeer.ListenAddresses.Select(a => a.ToString()).ToArray()
            };
            if (await _providerStore.AddProviderAsync(key, record, cancellationToken))
                successCount++;
        }

        var targetKey = new PublicKey(key);
        var closestNodes = await _kademlia.LookupNodesClosest(targetKey, cancellationToken);

        var tasks = closestNodes
            .Where(n => !n.Equals(_localDhtNode))
            .Take(_options.KSize)
            .Select(async node =>
            {
                try
                {
                    await _dhtMessageSender.AddProviderAsync(node, key, _localDhtNode, cancellationToken);
                    Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    _logger?.LogTrace("AddProvider to {NodeId} failed: {Error}", node.PeerId, ex.Message);
                }
            });

        await Task.WhenAll(tasks);

        _logger?.LogInformation("Provide announced to {Count} nodes for key {KeyHash}", successCount, KeyHashHex(key));

        if (successCount > 0)
            _locallyProvidedKeys[Convert.ToBase64String(key)] = key;

        return successCount > 0;
    }

    public async Task<IEnumerable<PeerId>> FindProvidersAsync(byte[] key, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty", nameof(key));
        if (count <= 0) throw new ArgumentException("Count must be positive", nameof(count));

        var providers = new ConcurrentDictionary<PeerId, byte>();

        // Check local store first
        var localProviders = await _providerStore.GetProvidersAsync(key, count, cancellationToken);
        foreach (var p in localProviders)
            providers.TryAdd(p.PeerId, 0);

        if (providers.Count >= count)
            return providers.Keys.Take(count);

        if (_lookupAlgo is null)
            return providers.Keys;

        var targetHash = _keyOperator.GetKeyHash(new PublicKey(key));

        await _lookupAlgo.Lookup(
            targetHash,
            _options.KSize,
            async (node, token) =>
            {
                if (node.Equals(_localDhtNode))
                    return _routingTable!.GetKNearestNeighbour(targetHash);

                var result = await _dhtMessageSender.GetProvidersAsync(node, key, token);
                foreach (var provider in result.Providers)
                    providers.TryAdd(provider.PeerId, 0);

                return result.CloserPeers;
            },
            cancellationToken);

        _logger?.LogInformation("FindProviders found {Count} providers for key {KeyHash}", providers.Count, KeyHashHex(key));
        return providers.Keys.Take(count);
    }

    #endregion

    #region ISessionProtocol Implementation

    public Task DialAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;

    public Task ListenAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;

    #endregion

    #region Maintenance Operations

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_kademlia is null) return;

        try
        {
            await Task.WhenAll(
                _kademlia.Run(cancellationToken),
                RunRepublishLoopAsync(cancellationToken),
                RunMaintenanceLoopAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("Kademlia background processes stopped");
        }
    }

    private async Task RunRepublishLoopAsync(CancellationToken cancellationToken)
    {
        var valueInterval = _options.ValueRepublishInterval;
        var providerInterval = _options.ProviderRepublishInterval;
        var minInterval = valueInterval < providerInterval ? valueInterval : providerInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(minInterval, cancellationToken);

            try
            {
                await RepublishValuesAsync(cancellationToken);
                await RepublishProvidersAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Republish cycle failed: {Error}", ex.Message);
            }
        }
    }

    private async Task RepublishValuesAsync(CancellationToken cancellationToken)
    {
        foreach (var kvp in _locallyPublishedValues)
        {
            var key = kvp.Value;
            var stored = await _valueStore.GetValueAsync(key, cancellationToken);
            if (stored is not { Value.Length: > 0 })
            {
                _locallyPublishedValues.TryRemove(kvp.Key, out _);
                continue;
            }

            try
            {
                await PutValueAsync(key, stored.Value, cancellationToken);
                _logger?.LogDebug("Republished value for key {KeyHash}", KeyHashHex(key));
            }
            catch (Exception ex)
            {
                _logger?.LogTrace("Failed to republish value for key {KeyHash}: {Error}", KeyHashHex(key), ex.Message);
            }
        }
    }

    private async Task RepublishProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var kvp in _locallyProvidedKeys)
        {
            try
            {
                await ProvideAsync(kvp.Value, cancellationToken);
                _logger?.LogDebug("Republished provider for key {KeyHash}", KeyHashHex(kvp.Value));
            }
            catch (Exception ex)
            {
                _logger?.LogTrace("Failed to republish provider for key {KeyHash}: {Error}", KeyHashHex(kvp.Value), ex.Message);
            }
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.MaintenanceInterval, cancellationToken);

            try
            {
                await PerformMaintenanceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Maintenance cycle failed: {Error}", ex.Message);
            }
        }
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        if (_kademlia is null) return;

        await _kademlia.Bootstrap(cancellationToken);
        _logger?.LogInformation("DHT bootstrap completed");
    }

    public void AddNode(DhtNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _kademlia?.AddOrRefresh(node);
    }

    public async Task PerformMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        int expiredValues = await _valueStore.CleanupExpiredValuesAsync(cancellationToken);
        int expiredProviders = await _providerStore.CleanupExpiredProvidersAsync(cancellationToken);

        if (expiredValues > 0 || expiredProviders > 0)
            _logger?.LogInformation("Maintenance: removed {Values} values, {Providers} providers", expiredValues, expiredProviders);
    }

    public Dictionary<string, object> GetStatistics() => new()
    {
        ["Mode"] = _options.Mode.ToString(),
        ["StoredValues"] = _valueStore.Count,
        ["ProviderKeys"] = _providerStore.KeyCount,
        ["TotalProviders"] = _providerStore.TotalProviderCount,
        ["LocalPeerId"] = _localPeer.Identity.PeerId.ToString()
    };

    #endregion

    private static string KeyHashHex(byte[] key) =>
        Convert.ToHexString(key).Substring(0, Math.Min(16, key.Length * 2));
}
