// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using ILogger = Libp2p.Protocols.KadDht.InternalTable.Logging.ILogger;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// This find nearest k query does not follow the kademlia paper faithfully. Instead of distinct rounds, it has
/// num worker where alpha is the number of worker. Worker does not wait for other worker. Stop condition
/// happens if no more node to query or no new node can be added to the current result set that can improve it
/// for more than alpha*2 request. It is slightly faster than the legacy query on find value where it can be cancelled
/// earlier as it converge to the content faster, but take more query for findnodes due to a more strict stop
/// condition.
/// </summary>
public class LookupKNearestNeighbour<TNode>(
    IRoutingTable<TNode, ValueHash256> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    INodeHealthTracker<TNode> nodeHealthTracker,
    KademliaConfig<TNode> config,
    Microsoft.Extensions.Logging.ILogger logger) : ILookupAlgo<TNode, ValueHash256> where TNode : notnull
{
    private readonly TimeSpan _findNeighbourHardTimeout = config.LookupFindNeighbourHardTimout;
    private readonly Microsoft.Extensions.Logging.ILogger _logger = logger;

    public async Task<TNode[]> Lookup(
        ValueHash256 targetKey,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    )
    {
        _logger.LogDebug("Initiate lookup for key {TargetKey}", targetKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;

        var queried = new ConcurrentDictionary<ValueHash256, TNode>();
        var seen = new ConcurrentDictionary<ValueHash256, TNode>();

        IComparer<ValueHash256> comparer = Comparer<ValueHash256>.Create((k1, k2) =>
            Hash256XorUtils.Compare(k1, k2, targetKey));
        IComparer<ValueHash256> comparerReverse = Comparer<ValueHash256>.Create((k1, k2) =>
            Hash256XorUtils.Compare(k2, k1, targetKey));

        McsLock queueLock = new McsLock();

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> bestSeen = new(comparer);

        // Ordered by highest distance. Added on result. Get popped as result.
        PriorityQueue<(ValueHash256, TNode), ValueHash256> finalResult = new(comparerReverse);

        foreach (TNode node in routingTable.GetKNearestNeighbour(targetKey, default))
        {
            ValueHash256 nodeKey = nodeHashProvider.GetHash(node);
            seen.TryAdd(nodeKey, node);
            bestSeen.Enqueue((nodeKey, node), nodeKey);
        }

        TaskCompletionSource roundComplete = new TaskCompletionSource(token);
        int closestNodeRound = 0;
        int currentRound = 0;
        int queryingTask = 0;
        bool finished = false;

        Task[] worker = Enumerable.Range(0, config.Alpha).Select(i => Task.Run(async () =>
        {
            while (!finished)
            {
                token.ThrowIfCancellationRequested();
                if (!TryGetNodeToQuery(out (ValueHash256 key, TNode node)? toQuery))
                {
                    if (queryingTask > 0)
                    {
                        await Task.WhenAny(roundComplete.Task, Task.Delay(100, token));
                        continue;
                    }
                    _logger.LogTrace("Stopping lookup. No node to query.");
                    break;
                }
                try
                {
                    if (ShouldStopDueToNoBetterResult(out var round))
                    {
                        _logger.LogTrace("Stopping lookup. No better result.");
                        break;
                    }
                    queried.TryAdd(toQuery.Value.key, toQuery.Value.node);
                    (TNode, TNode[]? neighbours)? result = await WrappedFindNeighbourOp(toQuery.Value.node);
                    if (result == null) continue;
                    ProcessResult(toQuery.Value.key, toQuery.Value.node, result, round);
                }
                finally
                {
                    Interlocked.Decrement(ref queryingTask);
                    if (roundComplete.TrySetResult()) roundComplete = new TaskCompletionSource(token);
                }
            }
        }, token)).ToArray();

        await Task.WhenAny(worker);
        finished = true;
        await cts.CancelAsync();

        return CompileResult();

        async Task<(TNode target, TNode[]? retVal)> WrappedFindNeighbourOp(TNode node)
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts2.CancelAfter(_findNeighbourHardTimeout);
            try
            {
                var ret = await findNeighbourOp(node, cts2.Token);
                nodeHealthTracker.OnIncomingMessageFrom(node);
                return (node, ret);
            }
            catch (OperationCanceledException e)
            {
                nodeHealthTracker.OnRequestFailed(node);
                _logger.LogWarning(e, "Find neighbour op failed.");
                return (node, null);
            }
            catch (Exception e)
            {
                nodeHealthTracker.OnRequestFailed(node);
                _logger.LogWarning(e, "Find neighbour op failed.");
                return (node, null);
            }
        }

        bool TryGetNodeToQuery(out (ValueHash256, TNode)? toQuery)
        {
            using McsLock.Disposable _ = queueLock.Acquire();
            if (bestSeen.Count == 0)
            {
                toQuery = default;
                return false;
            }
            Interlocked.Increment(ref queryingTask);
            toQuery = bestSeen.Dequeue();
            return true;
        }

        void ProcessResult(ValueHash256 key, TNode toQuery, (TNode, TNode[]? neighbours)? valueTuple, int round)
        {
            using var _ = queueLock.Acquire();
            finalResult.Enqueue((key, toQuery), key);
            while (finalResult.Count > k)
            {
                finalResult.Dequeue();
            }
            TNode[]? neighbours = valueTuple?.neighbours;
            if (neighbours == null) return;
            foreach (TNode neighbour in neighbours)
            {
                ValueHash256 neighbourKey = nodeHashProvider.GetHash(neighbour);
                if (queried.ContainsKey(neighbourKey)) continue;
                if (!seen.TryAdd(neighbourKey, neighbour)) continue;
                bestSeen.Enqueue((neighbourKey, neighbour), neighbourKey);
                if (closestNodeRound < round)
                {
                    if (finalResult.Count < k)
                    {
                        closestNodeRound = round;
                    }
                    if (finalResult.TryPeek(out (ValueHash256 key, TNode node) worstResult, out ValueHash256 _) && comparer.Compare(neighbourKey, worstResult.key) < 0)
                    {
                        closestNodeRound = round;
                    }
                }
            }
        }

        TNode[] CompileResult()
        {
            using var _ = queueLock.Acquire();
            if (finalResult.Count > k) finalResult.Dequeue();
            return finalResult.UnorderedItems.Select((kv) => kv.Element.Item2).ToArray();
        }

        bool ShouldStopDueToNoBetterResult(out int round)
        {
            using var _ = queueLock.Acquire();
            round = Interlocked.Increment(ref currentRound);
            if (finalResult.Count >= k && round - closestNodeRound >= (config.Alpha * 2))
            {
                _logger.LogTrace("No more closer node. Round: {Round}, closestNodeRound {ClosestNodeRound}", round, closestNodeRound);
                return true;
            }
            return false;
        }
    }
}
