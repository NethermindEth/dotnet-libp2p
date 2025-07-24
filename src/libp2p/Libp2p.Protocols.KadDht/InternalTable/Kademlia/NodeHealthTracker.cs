// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public class NodeHealthTracker<TKey, TNode>(
    KademliaConfig<TNode> config,
    IRoutingTable<TNode> routingTable,
    INodeHashProvider<TNode> nodeHashProvider,
    IKademliaMessageSender<TKey, TNode> kademliaMessageSender,
    ILogManager logManager
) : INodeHealthTracker<TNode> where TNode : notnull
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger = logManager.GetClassLogger<NodeHealthTracker<TKey, TNode>>();

    private readonly ConcurrentDictionary<ValueHash256, bool> _isRefreshing = new();
    private readonly LruCache<ValueHash256, int> _peerFailures = new(1024, "peer failure");
    private readonly ValueHash256 _currentNodeIdAsHash = nodeHashProvider.GetHash(config.CurrentNodeId);
    private readonly TimeSpan _refreshPingTimeout = config.RefreshPingTimeout;

    private bool SameAsSelf(TNode node)
    {
        return nodeHashProvider.GetHash(node) == _currentNodeIdAsHash;
    }

    private void TryRefresh(TNode toRefresh)
    {
        ValueHash256 nodeHash = nodeHashProvider.GetHash(toRefresh);
        if (_isRefreshing.TryAdd(nodeHash, true))
        {
            Task.Run(async () =>
            {
                // First, we delay in case any new message come and clear the refresh task, so we don't need to send any ping.
                await Task.Delay(100);
                if (!_isRefreshing.ContainsKey(nodeHash))
                {
                    return;
                }

                // OK, fine, we'll ping it.
                using CancellationTokenSource cts = new CancellationTokenSource(_refreshPingTimeout);
                try
                {
                    await kademliaMessageSender.Ping(toRefresh, cts.Token);
                    OnIncomingMessageFrom(toRefresh);
                }
                catch (OperationCanceledException)
                {
                    OnRequestFailed(toRefresh);
                }
                catch (Exception e)
                {
                    OnRequestFailed(toRefresh);
                    if (_logger.IsDebug) _logger.Debug($"Error while refreshing node {toRefresh}, {e}");
                }

                if (_isRefreshing.TryRemove(nodeHash, out _))
                {
                    routingTable.Remove(nodeHash);
                }
            });
        }
    }

    /// <summary>
    /// Call when an incoming message from a node is received. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnIncomingMessageFrom(TNode node)
    {
        _isRefreshing.TryRemove(nodeHashProvider.GetHash(node), out _);

        var addResult = routingTable.TryAddOrRefresh(nodeHashProvider.GetHash(node), node, out TNode? toRefresh);
        if (addResult == BucketAddResult.Full && toRefresh != null)
        {
            if (SameAsSelf(toRefresh))
            {
                // Move the current node entry to the front of its bucket.
                routingTable.TryAddOrRefresh(_currentNodeIdAsHash, node, out TNode? _);
            }
            else
            {
                TryRefresh(toRefresh);
            }
        }
        _peerFailures.Delete(nodeHashProvider.GetHash(node));
    }

    /// <summary>
    /// Call when a requset to a node failed. This is used by other algorithm for health checks.
    /// </summary>
    /// <param name="node"></param>
    public void OnRequestFailed(TNode node)
    {
        ValueHash256 hash = nodeHashProvider.GetHash(node);
        if (!_peerFailures.TryGet(hash, out var currentFailure))
        {
            _peerFailures.Set(hash, 1);
            return;
        }

        if (currentFailure >= config.NodeRequestFailureThreshold)
        {
            routingTable.Remove(hash);
            _peerFailures.Delete(hash);
        }

        _peerFailures.Set(hash, currentFailure + 1);
    }
}


