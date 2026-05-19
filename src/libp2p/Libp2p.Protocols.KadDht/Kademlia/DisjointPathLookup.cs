// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;

namespace Libp2p.Protocols.KadDht.Kademlia;

/// <summary>
/// Disjoint path lookup for Sybil resistance per the S/Kademlia paper extension.
/// Runs <c>disjointPaths</c> independent lookups in parallel, each with a distinct
/// peer set. The final result merges all paths and keeps the K closest nodes overall.
/// <para>
/// This makes it significantly harder for an attacker to eclipse a lookup because
/// they would need to control nodes on every independent path simultaneously.
/// </para>
/// </summary>
public class DisjointPathLookup<THash, TNode> : ILookupAlgo<THash, TNode>
    where TNode : notnull
    where THash : struct, IKademliaHash<THash>
{
    private readonly ILookupAlgo<THash, TNode> _innerLookup;
    private readonly INodeHashProvider<THash, TNode> _nodeHashProvider;
    private readonly int _disjointPaths;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a disjoint path lookup wrapper.
    /// </summary>
    /// <param name="innerLookup">The underlying lookup algorithm (e.g., <see cref="LookupKNearestNeighbour{THash,TNode}"/>).</param>
    /// <param name="nodeHashProvider">Hash provider for distance calculations.</param>
    /// <param name="disjointPaths">Number of independent paths to run (S parameter, typically 2-10).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public DisjointPathLookup(
        ILookupAlgo<THash, TNode> innerLookup,
        INodeHashProvider<THash, TNode> nodeHashProvider,
        int disjointPaths,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(innerLookup);
        ArgumentNullException.ThrowIfNull(nodeHashProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (disjointPaths < 2)
            throw new ArgumentOutOfRangeException(nameof(disjointPaths), "Must be at least 2 for disjoint paths to be effective.");

        _innerLookup = innerLookup;
        _nodeHashProvider = nodeHashProvider;
        _disjointPaths = disjointPaths;
        _logger = loggerFactory.CreateLogger<DisjointPathLookup<THash, TNode>>();
    }

    public async Task<TNode[]> Lookup(
        THash targetHash,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token)
    {
        _logger.LogDebug("Starting disjoint lookup with {Paths} paths for K={K}", _disjointPaths, k);

        // Each path tracks its own set of queried nodes to prevent overlap.
        // A global seen set ensures no two paths query the same node.
        var globalQueried = new System.Collections.Concurrent.ConcurrentDictionary<THash, byte>();

        var pathTasks = new Task<TNode[]>[_disjointPaths];

        for (int i = 0; i < _disjointPaths; i++)
        {
            int pathIndex = i;
            pathTasks[i] = Task.Run(async () =>
            {
                try
                {
                    return await _innerLookup.Lookup(
                        targetHash,
                        k,
                        async (node, ct) =>
                        {
                            // Ensure this node hasn't already been queried by another path
                            THash nodeHash = _nodeHashProvider.GetHash(node);
                            if (!globalQueried.TryAdd(nodeHash, 0))
                            {
                                // Another path already queried this node — return empty
                                // to avoid overlapping paths
                                return Array.Empty<TNode>();
                            }

                            return await findNeighbourOp(node, ct);
                        },
                        token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return Array.Empty<TNode>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Disjoint path {PathIndex} failed", pathIndex);
                    return Array.Empty<TNode>();
                }
            }, token);
        }

        var allResults = await Task.WhenAll(pathTasks);

        // Merge: collect unique nodes from all paths, keep K closest to target
        var mergedNodes = new Dictionary<THash, TNode>();
        foreach (var pathResult in allResults)
        {
            foreach (var node in pathResult)
            {
                THash hash = _nodeHashProvider.GetHash(node);
                mergedNodes.TryAdd(hash, node);
            }
        }

        var comparer = Comparer<THash>.Create((h1, h2) =>
            THash.Compare(h1, h2, targetHash));

        var result = mergedNodes
            .OrderBy(kv => kv.Key, comparer)
            .Take(k)
            .Select(kv => kv.Value)
            .ToArray();

        _logger.LogDebug("Disjoint lookup completed: {TotalUnique} unique nodes across {Paths} paths, returning {Count}",
            mergedNodes.Count, _disjointPaths, result.Length);

        return result;
    }
}
