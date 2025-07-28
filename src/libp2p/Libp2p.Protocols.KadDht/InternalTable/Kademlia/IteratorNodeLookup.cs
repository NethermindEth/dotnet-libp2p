// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Special lookup made specially for node discovery as the standard lookup is too slow or unnecessarily parallelized.
/// Instead of returning k closest node, it just returns the nodes that it found along the way and stopped early.
/// This is useful for node discovery as trying to get the k closest node is not completely necessary, as the main goal
/// is to reach all node. The lookup is not parallelized as it is expected to be parallelized at a higher level with
/// each worker having different target to look into.
/// </summary>
public class IteratorNodeLookup<TNode, TKey>(
    IRoutingTable<TNode, TKey> routingTable,
    KademliaConfig<TNode> kademliaConfig,
    IKademliaMessageSender<TNode, TKey> msgSender,
    IKeyOperator<TNode, TKey> keyOperator,
    ILogger<IteratorNodeLookup<TNode, TKey>> logger) : IIteratorNodeLookup<TNode, TKey> where TNode : notnull
{
    private readonly ILogger _logger = logger;
    private readonly TKey _currentNodeIdAsKey;

    // Small lru of unreachable nodes, prevent retrying. Pretty effective, although does not improve discovery overall.
    private readonly LruCache<TKey, DateTimeOffset> _unreacheableNodes = new(256, TimeSpan.FromMinutes(5));

    // The maximum round per lookup. Higher means that it will 'see' deeper into the network, but come at a latency
    // cost of trying many node for increasingly lower new node.
    private const int MaxRounds = 3;

    // These two dont come into effect as MaxRounds is low.
    private const int MaxNonProgressingRound = 3;
    private const int MinResult = 128;

    private bool SameAsSelf(TNode node)
    {
        return EqualityComparer<TKey>.Default.Equals(keyOperator.GetKey(node), _currentNodeIdAsKey);
    }

    public async IAsyncEnumerable<TNode> Lookup(TKey target, [EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Initiate lookup for key {Key}", target);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;

        ConcurrentDictionary<TKey, TNode> queried = new();
        ConcurrentDictionary<TKey, TNode> seen = new();

        IComparer<TKey> comparer = Comparer<TKey>.Create((k1, k2) =>
            keyOperator.GetDistance(k1, k2));

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(TKey, TNode), TKey> queryQueue = new(comparer);

        // Used to determine if the worker should stop
        TKey? bestNodeKey = default;
        int closestNodeRound = 0;
        int currentRound = 0;
        int totalResult = 0;

        // Check internal table first
        foreach (TNode node in routingTable.GetKNearestNeighbour(target, default))
        {
            TKey nodeKey = keyOperator.GetKey(node);
            seen.TryAdd(nodeKey, node);

            queryQueue.Enqueue((nodeKey, node), nodeKey);

            yield return node;

            if (bestNodeKey == null || comparer.Compare(nodeKey, bestNodeKey) < 0)
            {
                bestNodeKey = nodeKey;
            }
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!queryQueue.TryDequeue(out (TKey key, TNode node) toQuery, out TKey key))
            {
                // No node to query and running query.
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Stopping lookup. No node to query.");
                yield break;
            }

            if (SameAsSelf(toQuery.node)) continue;

            queried.TryAdd(toQuery.key, toQuery.node);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Query {Node} at round {Round}", toQuery.node, currentRound);

            TNode[]? neighbours = await FindNeighbour(toQuery.node, target, token);
            if (neighbours == null || neighbours?.Length == 0)
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Empty result");
                continue;
            }

            int queryIgnored = 0;
            int seenIgnored = 0;
            foreach (TNode neighbour in neighbours!)
            {
                TKey neighbourKey = keyOperator.GetKey(neighbour);

                // Already queried, we ignore
                if (queried.ContainsKey(neighbourKey))
                {
                    queryIgnored++;
                    continue;
                }

                // When seen already dont record
                if (!seen.TryAdd(neighbourKey, neighbour))
                {
                    seenIgnored++;
                    continue;
                }

                totalResult++;
                yield return neighbour;

                bool foundBetter = bestNodeKey != null && comparer.Compare(neighbourKey, bestNodeKey) < 0;
                queryQueue.Enqueue((neighbourKey, neighbour), neighbourKey);

                // If found a better node, reset closes node round.
                // This causes `ShouldStopDueToNoBetterResult` to return false.
                if (closestNodeRound < currentRound && foundBetter)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Found better neighbour {Neighbour} at round {Round}.", neighbour, currentRound);
                    bestNodeKey = neighbourKey;
                    closestNodeRound = currentRound;
                }
            }

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Count {Count}, queried {Queried}, seen {Seen}", neighbours.Length, queryIgnored, seenIgnored);

            if (ShouldStop())
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Stopping lookup. No better result.");
                break;
            }
        }

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Lookup operation finished.");
        yield break;

        bool ShouldStop()
        {
            int round = ++currentRound;
            if (totalResult >= MinResult && round - closestNodeRound >= MaxNonProgressingRound)
            {
                // No closer node for more than or equal to _alpha*2 round.
                // Assume exit condition
                // Why not just _alpha?
                // Because there could be currently running work that may increase closestNodeRound.
                // So including this worker, assume no more
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("No more closer node. Round: {Round}, closestNodeRound {ClosestNodeRound}", round, closestNodeRound);
                return true;
            }

            if (round >= MaxRounds)
            {
                return true;
            }

            return false;
        }
    }

    async Task<TNode[]?> FindNeighbour(TNode node, TKey target, CancellationToken token)
    {
        try
        {
            TKey nodeKey = keyOperator.GetKey(node);
            if (_unreacheableNodes.TryGet(nodeKey, out var lastAttempt) &&
                lastAttempt + TimeSpan.FromMinutes(5) > DateTimeOffset.Now)
            {
                return [];
            }

            return await msgSender.FindNeighbours(node, target, token);
        }
        catch (OperationCanceledException)
        {
            _unreacheableNodes.Set(keyOperator.GetKey(node), DateTimeOffset.Now);
            return null;
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Find neighbour op failed. {Error}", e);
            return null;
        }
    }
}
