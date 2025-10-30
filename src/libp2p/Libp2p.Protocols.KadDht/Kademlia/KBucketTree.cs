// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using System.Text;

namespace Libp2p.Protocols.KadDht.Kademlia;

public class KBucketTree<THash, TNode> : IRoutingTable<THash, TNode> where TNode : notnull where THash : struct, IKademiliaHash<THash>
{
    private class TreeNode
    {
        public KBucket<THash, TNode> Bucket { get; }
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public THash Prefix { get; }
        public bool IsLeaf => Left == null && Right == null;

        public TreeNode(int k, THash prefix)
        {
            Bucket = new KBucket<THash, TNode>(k);
            Prefix = prefix;
        }
    }

    private readonly TreeNode _root;
    private readonly int _b;
    private readonly int _k;
    private readonly THash _currentNodeHash;
    private readonly ILogger _logger;
    private const int MaxAutoSplitDepth = 12; // legacy shallow auto split hint (for early population)
    private const int MaxHealthyDepth = 24;   // hard cap to avoid pathological deep splitting in low-diversity demos
    private long _splitCount;
    private long _evictionAttempts;
    private long _skippedEmptySideSplits;

    // TODO: Double check and probably make lockless
    private readonly McsLock _lock = new McsLock();

    public KBucketTree(KademliaConfig<TNode> config, INodeHashProvider<THash, TNode> nodeHashProvider, ILoggerFactory logManager)
    {
        _k = config.KSize;
        _b = config.Beta;
        _currentNodeHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        _root = new TreeNode(config.KSize, new THash());
        _logger = logManager.CreateLogger<KBucketTree<THash, TNode>>();
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Initialized KBucketTree with k={_k}, currentNodeId={_currentNodeHash}");
    }

    public BucketAddResult TryAddOrRefresh(in THash nodeHash, TNode node, out TNode? toRefresh)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (nodeHash.Equals(_currentNodeHash))
        {
            toRefresh = default;
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Ignoring attempt to add self node");
            return BucketAddResult.Refreshed; // treat as no-op
        }

        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Adding node {node} with XOR distance {THash.XorDistance(_currentNodeHash, nodeHash)}");

        TreeNode current = _root;
        // As in, what would be the depth of the node assuming all branch on the traversal is populated.
        int logDistance = THash.MaxDistance - THash.CalculateLogDistance(_currentNodeHash, nodeHash);
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Reached leaf node at depth {depth}");
                var resp = current.Bucket.TryAddOrRefresh(nodeHash, node, out toRefresh);
                if (resp == BucketAddResult.Added)
                {
                    OnNodeAdded?.Invoke(this, node);
                }
                if (resp is BucketAddResult.Added or BucketAddResult.Refreshed)
                {
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Successfully added/refreshed node {node} in bucket at depth {depth}");
                    if ((_splitCount + _evictionAttempts) % 100 == 0)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug)) LogTreeStatistics();
                    }
                    return resp;
                }

                if (resp == BucketAddResult.Full && ShouldSplit(depth, current.Prefix, current.Bucket))
                {
                    if (TrySplitBucket(depth, current))
                    {
                        continue; // retry insertion at this depth with new children
                    }
                    // else fall through to eviction attempt accounting
                }

                if (resp == BucketAddResult.Full) _evictionAttempts++;

                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"Failed to add node {Short(nodeHash)} {node}. Bucket at depth {depth} is full. {_k} {current.Bucket.GetAllWithHash().Count()}");
                return resp;
            }

            bool goRight = GetBit(nodeHash, depth);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    public TNode? GetByHash(THash hash)
    {
        return GetBucketForHash(hash).GetByHash(hash);
    }

    private KBucket<THash, TNode> GetBucketForHash(THash nodeHash)
    {
        TreeNode current = _root;
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                _logger.LogDebug($"Reached leaf node at depth {depth}");
                return current.Bucket;
            }

            bool goRight = GetBit(nodeHash, depth);
            _logger.LogDebug($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private bool ShouldSplit(int depth, THash bucketPrefix, KBucket<THash, TNode> bucket)
    {
        if (depth >= MaxHealthyDepth) return false; // depth cap
        bool containsSelf = PrefixMatches(_currentNodeHash, bucketPrefix, depth);
        if (!containsSelf) return false; // only split buckets that contain self for now
        // Require some diversity hint: at least half capacity or presence of two distinct next-bit buckets
        // (we check actual partition later before committing)
        bool densityOk = bucket.Count >= _k; // bucket is full already when this is called
        bool shouldSplit = depth < THash.MaxDistance && densityOk;
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"ShouldSplit at depth {depth}: {shouldSplit} (containsSelf={containsSelf}, densityOk={densityOk})");
        return shouldSplit;
    }

    private bool PrefixMatches(THash hash, THash prefix, int depth)
    {
        // Compare the first `depth` bits of hash and prefix.
        for (int i = 0; i < depth; i++)
        {
            if (GetBit(hash, i) != GetBit(prefix, i)) return false;
        }
        return true;
    }

    private bool TrySplitBucket(int depth, TreeNode node)
    {
        // Pre-partition to ensure both sides would be non-empty
        var leftEntries = new List<(THash, TNode)>();
        var rightEntries = new List<(THash, TNode)>();
        foreach (var item in node.Bucket.GetAllWithHash())
        {
            (GetBit(item.Item1, depth) ? rightEntries : leftEntries).Add(item);
        }
        if (leftEntries.Count == 0 || rightEntries.Count == 0)
        {
            _skippedEmptySideSplits++;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug($"Skipping split at depth {depth} due to empty side (left={leftEntries.Count}, right={rightEntries.Count})");
                if (_logger.IsEnabled(LogLevel.Trace) && leftEntries.Count + rightEntries.Count > 0)
                {
                    var allHashes = leftEntries.Concat(rightEntries).Select(x => Convert.ToHexString(x.Item1.Bytes, 0, Math.Min(4, x.Item1.Bytes.Length))).Take(5);
                    _logger.LogTrace($"Sample hashes at depth {depth}: {string.Join(", ", allHashes)}");
                    _logger.LogTrace($"Current node prefix: {Convert.ToHexString(node.Prefix.Bytes, 0, Math.Min(4, node.Prefix.Bytes.Length))}");

                    // Show bit analysis for first few nodes
                    var firstFew = leftEntries.Concat(rightEntries).Take(3);
                    foreach (var (hash, peer) in firstFew)
                    {
                        bool bit = GetBit(hash, depth);
                        _logger.LogTrace($"Node {Convert.ToHexString(hash.Bytes, 0, Math.Min(4, hash.Bytes.Length))} bit at depth {depth}: {(bit ? 1 : 0)} (obj: {peer.GetHashCode()})");
                    }

                    // Show all unique hashes
                    var uniqueHashes = leftEntries.Concat(rightEntries).Select(x => Convert.ToHexString(x.Item1.Bytes, 0, Math.Min(4, x.Item1.Bytes.Length))).Distinct().ToList();
                    _logger.LogTrace($"Unique hash prefixes: {string.Join(", ", uniqueHashes)} (total: {uniqueHashes.Count})");
                }
            }
            return false;
        }

        node.Left = new TreeNode(_k, node.Prefix);
        var rightPrefixBytes = node.Prefix.Bytes.ToArray();
        rightPrefixBytes[depth / 8] |= (byte)(1 << (7 - (depth % 8)));
        node.Right = new TreeNode(_k, THash.FromBytes(rightPrefixBytes));

        _logger.LogDebug($"Created children at depth {depth + 1}");
        _splitCount++;

        // Insert pre-partitioned entries (reverse to preserve recency ordering roughly)
        foreach (var item in leftEntries.AsEnumerable().Reverse())
            node.Left.Bucket.TryAddOrRefresh(item.Item1, item.Item2, out _);
        foreach (var item in rightEntries.AsEnumerable().Reverse())
            node.Right.Bucket.TryAddOrRefresh(item.Item1, item.Item2, out _);

        node.Bucket.Clear();
        _logger.LogDebug($"Finished splitting bucket. Left count: {node.Left.Bucket.Count}, Right count: {node.Right.Bucket.Count}");
        return true;
    }

    public bool Remove(in THash nodeHash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.LogDebug($"Attempting to remove node {nodeHash} with hash {nodeHash}");

        return GetBucketForHash(nodeHash).RemoveAndReplace(nodeHash);
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.LogDebug($"Getting all nodes at distance {distance}");
        List<TNode> result = new List<TNode>();
        GetAllAtDistanceRecursive(_root, 0, distance, result);
        _logger.LogDebug($"Found {result.Count} nodes at distance {distance}");
        return result.ToArray();
    }

    private void GetAllAtDistanceRecursive(TreeNode node, int depth, int distance, List<TNode> result)
    {
        int targetDepth = THash.MaxDistance - distance;
        if (node.IsLeaf)
        {
            if (depth <= targetDepth)
            {
                result.AddRange(node.Bucket.GetAllWithHash()
                    .Where(kv => THash.CalculateLogDistance(kv.Item1, _currentNodeHash) == distance)
                    .Select(kv => kv.Item2));
            }
            else
            {
                result.AddRange(node.Bucket.GetAll());
            }
        }
        else
        {
            if (depth < targetDepth)
            {
                bool goRight = GetBit(_currentNodeHash, depth);
                if (goRight)
                {
                    GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result);
                }
                else
                {
                    GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result);
                }
            }
            else if (depth == targetDepth)
            {
                bool goRight = GetBit(_currentNodeHash, depth);
                // Note: We go the opposite direction here, as the same direction would have a distance + 1
                if (goRight)
                {
                    GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result);
                }
                else
                {
                    GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result);
                }
            }
            else
            {
                GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result);
                GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result);
            }
        }
    }

    public IEnumerable<(THash Prefix, int Distance, KBucket<THash, TNode> Bucket)> IterateBuckets()
    {
        using McsLock.Disposable _ = _lock.Acquire();

        // Well, it need to ToArray, otherwise the lock does not really do anything.
        return DoIterateBucketRandomHashes(_root, 0).ToArray();
    }

    private IEnumerable<(THash Prefix, int Distance, KBucket<THash, TNode> Bucket)> DoIterateBucketRandomHashes(TreeNode node, int depth)
    {
        if (node.IsLeaf)
        {
            yield return (node.Prefix, depth, node.Bucket);
        }
        else
        {
            foreach (var bucketInfo in DoIterateBucketRandomHashes(node.Left!, depth + 1))
            {
                yield return bucketInfo;
            }

            foreach (var bucketInfo in DoIterateBucketRandomHashes(node.Right!, depth + 1))
            {
                yield return bucketInfo;
            }
        }
    }

    private IEnumerable<(THash, TNode)> IterateNeighbour(THash hash)
    {
        foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(_root, 0, hash))
        {
            foreach ((THash, TNode) entry in treeNode.Bucket.GetAllWithHash())
            {
                yield return entry;
            }
        }
    }

    private IEnumerable<TreeNode> IterateNodeFromClosestToTarget(TreeNode currentNode, int depth, THash target)
    {
        if (currentNode.IsLeaf)
        {
            yield return currentNode;
        }
        else
        {
            if (GetBit(target, depth))
            {
                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Right!, depth + 1, target))
                {
                    yield return treeNode;
                }

                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Left!, depth + 1, target))
                {
                    yield return treeNode;
                }
            }
            else
            {
                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Left!, depth + 1, target))
                {
                    yield return treeNode;
                }

                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Right!, depth + 1, target))
                {
                    yield return treeNode;
                }
            }
        }
    }

    public TNode[] GetKNearestNeighbour(THash hash, THash? exclude, bool excludeSelf)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        KBucket<THash, TNode> firstBucket = GetBucketForHash(hash);
        bool shouldNotContainExcludedNode = exclude == null || !firstBucket.ContainsNode(exclude.Value);
        bool shouldNotContainSelf = excludeSelf == false || !firstBucket.ContainsNode(_currentNodeHash);

        if (shouldNotContainExcludedNode && shouldNotContainSelf)
        {
            TNode[] nodes = firstBucket.GetAll();
            if (nodes.Length == _k)
            {
                // Fast path. In theory, most of the time, this would be the taken path, where no array
                // concatenation or creation is needed.
                return nodes;
            }
        }

        var iterator = IterateNeighbour(hash);

        if (exclude != null)
            iterator = iterator
                .Where(kv => !kv.Item1.Equals(exclude.Value));

        if (excludeSelf)
            iterator = iterator
                .Where(kv => !kv.Item1.Equals(_currentNodeHash));

        return iterator.Take(_k)
            .Select(kv => kv.Item2)
            .ToArray();
    }

    private bool GetBit(THash hash, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (hash.Bytes[byteIndex] & (1 << (7 - bitIndex))) != 0;
    }

    private void LogTreeStructureRecursive(TreeNode node, string indent, bool last, int depth, StringBuilder sb)
    {
        sb.Append(indent);
        if (last)
        {
            sb.Append("└─");
            indent += "  ";
        }
        else
        {
            sb.Append("├─");
            indent += "│ ";
        }

        if (node.Left == null && node.Right == null)
        {
            sb.AppendLine($"Bucket (Depth: {depth}, Count: {node.Bucket.Count})");
            return;
        }

        sb.AppendLine($"Node (Depth: {depth})");
        LogTreeStructureRecursive(node.Left!, indent, false, depth + 1, sb);
        LogTreeStructureRecursive(node.Right!, indent, true, depth + 1, sb);
    }

    private void LogTreeStatistics()
    {
        int totalNodes = 0;
        int totalBuckets = 0;
        int maxDepth = 0;
        int totalItems = 0;

        void TraverseTree(TreeNode node, int depth)
        {
            totalNodes++;
            maxDepth = Math.Max(maxDepth, depth);

            if (node.Left == null && node.Right == null)
            {
                totalBuckets++;
                totalItems += node.Bucket.Count;
            }
            else
            {
                TraverseTree(node.Left!, depth + 1);
                TraverseTree(node.Right!, depth + 1);
            }
        }

        TraverseTree(_root, 0);

        _logger.LogDebug($"Tree Statistics:\n" +
                 $"Total Nodes: {totalNodes}\n" +
                 $"Total Buckets: {totalBuckets}\n" +
                 $"Max Depth: {maxDepth}\n" +
                 $"Total Items: {totalItems}\n" +
                 $"Average Items per Bucket: {(double)totalItems / totalBuckets:F2}\n" +
                 $"Splits: {_splitCount}\n" +
                 $"EvictionAttempts: {_evictionAttempts}\n" +
                 $"SkippedEmptySideSplits: {_skippedEmptySideSplits}");
    }
    private static string Short(THash h)
    {
        var b = h.Bytes;
        return Convert.ToHexString(b, 0, Math.Min(3, b.Length));
    }
    private void LogTreeStructure()
    {
        StringBuilder sb = new StringBuilder();
        LogTreeStructureRecursive(_root, "", true, 0, sb);
        _logger.LogInformation($"Current Tree Structure:\n{sb}");
    }

    public void LogDebugInfo()
    {
        LogTreeStatistics();
        LogTreeStructure();
    }

    public event EventHandler<TNode>? OnNodeAdded;

    public int Size
    {
        get
        {
            int total = 0;
            foreach (var iterateBucket in IterateBuckets())
            {
                total += iterateBucket.Bucket.Count;
            }
            return total;
        }
    }
}
