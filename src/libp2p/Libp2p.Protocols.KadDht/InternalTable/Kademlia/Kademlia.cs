// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Microsoft.Extensions.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;



using System.Diagnostics;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public class Kademlia<TKey, TNode> : IKademlia<TKey, TNode> where TNode : notnull
{
    private readonly IKademliaMessageSender<TKey, TNode> _kademliaMessageSender;
    private readonly IKeyOperator<TKey, TNode> _keyOperator;
    private readonly IRoutingTable<TNode> _routingTable;
    private readonly ILookupAlgo<TNode> _lookupAlgo;
    private readonly INodeHealthTracker<TNode> _nodeHealthTracker;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private readonly TNode _currentNodeId;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly TimeSpan _refreshInterval;
    private readonly IReadOnlyList<TNode> _bootNodes;

    public Kademlia(
        IKeyOperator<TKey, TNode> keyOperator,
        IKademliaMessageSender<TKey, TNode> sender,
        IRoutingTable<TNode> routingTable,
        ILookupAlgo<TNode> lookupAlgo,
        Microsoft.Extensions.Logging.ILogger<Kademlia<TKey, TNode>> logger,
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
        _currentNodeIdAsHash = _keyOperator.GetNodeHash(_currentNodeId);
        _kSize = config.KSize;
        _refreshInterval = config.RefreshInterval;
        _bootNodes = config.BootNodes;

        AddOrRefresh(_currentNodeId);
    }

    public TNode CurrentNode => _currentNodeId;

    public void AddOrRefresh(TNode node)
    {
        // It add to routing table and does the whole refresh logid.
        _nodeHealthTracker.OnIncomingMessageFrom(node);
    }

    public void Remove(TNode node)
    {
        _routingTable.Remove(_keyOperator.GetNodeHash(node));
    }

    public TNode[] GetAllAtDistance(int i)
    {
        return _routingTable.GetAllAtDistance(i);
    }

    private bool SameAsSelf(TNode node)
    {
        return _keyOperator.GetNodeHash(node) == _currentNodeIdAsHash;
    }

    public Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken token, int? k = null)
    {
        return _lookupAlgo.Lookup(
            _keyOperator.GetKeyHash(key),
            k ?? _kSize,
            async (nextNode, token) =>
            {
                if (SameAsSelf(nextNode))
                {
                    ValueHash256 keyHash = _keyOperator.GetKeyHash(key);
                    return _routingTable.GetKNearestNeighbour(keyHash);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, key, token);
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

        // Check bootnodes is online
        await Parallel.ForEachAsync(_bootNodes, token, async (node, token) =>
        {
            try
            {
                // Should be added on Pong.
                await _kademliaMessageSender.Ping(node, token);
                onlineBootNodes++;
            }
            catch (OperationCanceledException)
            {
                // Unreachable
            }
        });

        if (_logger.IsEnabled(LogLevel.Information)) 
            _logger.LogInformation("Online bootnodes: {OnlineBootNodes}", onlineBootNodes);

        TKey currentNodeIdAsKey = _keyOperator.GetKey(_currentNodeId);
        await LookupNodesClosest(currentNodeIdAsKey, token);

        token.ThrowIfCancellationRequested();

        // Refreshes all bucket. one by one. That is not empty.
        // A refresh means to do a k-nearest node lookup for a random hash for that particular bucket.
        foreach ((ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket) in _routingTable.IterateBuckets())
        {
            var keyToLookup = _keyOperator.CreateRandomKeyAtDistance(Prefix, Distance);
            await LookupNodesClosest(keyToLookup, token);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug($"Bootstrap completed. Took {sw}.");
            _routingTable.LogDebugInfo();
        }
    }

    public TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false)
    {
        ValueHash256? excludeHash = null;
        if (excluding != null) excludeHash = _keyOperator.GetNodeHash(excluding);
        ValueHash256 hash = _keyOperator.GetKeyHash(target);
        return _routingTable.GetKNearestNeighbour(hash, excludeHash, excludeSelf);
    }

    public event EventHandler<TNode> OnNodeAdded
    {
        add => _routingTable.OnNodeAdded += value;
        remove => _routingTable.OnNodeAdded -= value;
    }

    public IEnumerable<TNode> IterateNodes()
    {
        foreach ((ValueHash256 _, int _, KBucket<TNode> Bucket) in _routingTable.IterateBuckets())
        {
            foreach (var node in Bucket.GetAll())
            {
                yield return node;
            }
        }
    }
}


