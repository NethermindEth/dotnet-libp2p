// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public class Kademlia<TNode, TKey> : IKademlia<TNode, TKey> where TNode : notnull
{
    private readonly IKademliaMessageSender<TNode, TKey> _kademliaMessageSender;
    private readonly IKeyOperator<TNode, TKey> _keyOperator;
    private readonly IRoutingTable<TNode, TKey> _routingTable;
    private readonly ILookupAlgo<TNode, TKey> _lookupAlgo;
    private readonly INodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly TKey _currentNodeIdAsKey;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;
    private readonly IReadOnlyList<TNode> _bootNodes;

    public Kademlia(
        IKeyOperator<TNode, TKey> keyOperator,
        IKademliaMessageSender<TNode, TKey> sender,
        IRoutingTable<TNode, TKey> routingTable,
        ILookupAlgo<TNode, TKey> lookupAlgo,
        ILogger<Kademlia<TNode, TKey>> logger,
        INodeHealthTracker<TNode> nodeHealthTracker,
        KademliaConfig<TNode> config)
    {
        _keyOperator = keyOperator;
        _kademliaMessageSender = sender;
        _routingTable = routingTable;
        _lookupAlgo = lookupAlgo;
        _nodeHealthTracker = nodeHealthTracker;
        _logger = logger;

        _currentNodeId = config.CurrentNodeId;
        _currentNodeIdAsKey = _keyOperator.GetKey(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;
        _bootNodes = config.BootNodes;

        AddOrRefresh(_currentNodeId);
    }

    public TNode CurrentNode => _currentNodeId;

    public void AddOrRefresh(TNode node)
    {
        // It add to routing table and does the whole refresh logic.
        _nodeHealthTracker.OnIncomingMessageFrom(node);
    }

    public void Remove(TNode node)
    {
        _routingTable.Remove(_keyOperator.GetKey(node));
    }

    public TNode[] GetAllAtDistance(int i)
    {
        return _routingTable.GetAllAtDistance(i);
    }

    private bool SameAsSelf(TNode node)
    {
        // Use null-conditional and EqualityComparer for safety
        var key = _keyOperator.GetKey(node);
        return key != null && EqualityComparer<TKey>.Default.Equals(key, _currentNodeIdAsKey);
    }

    public Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken? token, int? k = null)
    {
        return _lookupAlgo.Lookup(
            key,
            k ?? _kSize,
            async (nextNode, innerToken) =>
            {
                if (SameAsSelf(nextNode))
                {
                    return _routingTable.GetKNearestNeighbour(key);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, key, innerToken);
            },
            token
        );
    }

    public async Task Run(CancellationToken token)
    {
        while (true)
        {
            await Bootstrap(token);
            // The main loop can potentially be parallelized with multiple concurrent lookups to improve efficiency.

            await Task.Delay(_refreshInterval, token);
        }
    }

    public async Task Bootstrap(CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int onlineBootNodes = 0;
        // Avoid parameter hiding
        await Parallel.ForEachAsync(_bootNodes, token, async (node, innerToken) =>
        {
            try
            {
                await _kademliaMessageSender.Ping(node, innerToken);
                onlineBootNodes++;
            }
            catch (OperationCanceledException)
            {
                // Unreachable
            }
        });
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Online bootnodes: {OnlineBootNodes}", onlineBootNodes);
        await LookupNodesClosest(_currentNodeIdAsKey, token);
        token.ThrowIfCancellationRequested();
        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        foreach ((TKey prefix, int distance, KBucket<TNode> _) in _routingTable.IterateBuckets())
        {
            var keyBytes = _keyOperator.CreateRandomKeyAtDistance(prefix, distance);
            var keyToLookup = _keyOperator.GetKeyFromBytes<TKey>(keyBytes); // Specify TKey explicitly
            var nodes = await LookupNodesClosest(keyToLookup, token);
            _logger.LogInformation("Lookup nodes closest for bucket {Prefix} {Distance} {Nodes}", prefix, distance, nodes.Length);
        }
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug($"Bootstrap completed. Took {sw}.");
            _routingTable.LogDebugInfo();
        }
    }

    public TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false)
    {
        TKey excludeKey = excluding is not null ? _keyOperator.GetKey(excluding) : default!;
        return _routingTable.GetKNearestNeighbour(target, excludeKey, excludeSelf);
    }

    public event EventHandler<TNode> OnNodeAdded
    {
        add => _routingTable.OnNodeAdded += value;
        remove => _routingTable.OnNodeAdded -= value;
    }

    public IEnumerable<TNode> IterateNodes()
    {
        foreach ((TKey _, int _, KBucket<TNode> Bucket) in _routingTable.IterateBuckets())
        {
            foreach (var node in Bucket.GetAll())
            {
                yield return node;
            }
        }
    }
}
