// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Threading;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public class KBucketTree<TNode> : IRoutingTable<TNode, ValueHash256> where TNode : notnull
{
    private class TreeNode
    {
        public KBucket<TNode> Bucket { get; }
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public ValueHash256 Prefix { get; }
        public bool IsLeaf => Left == null && Right == null;

        public TreeNode(int k, ValueHash256 prefix)
        {
            Bucket = new KBucket<TNode>(k);
            Prefix = prefix;
        }
    }

    private readonly TreeNode _root;
    private readonly int _b;
    private readonly int _k;
    private readonly ValueHash256 _currentNodeHash;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    // TODO: Double check and probably make lockless
    private readonly McsLock _lock = new McsLock();

    public KBucketTree(KademliaConfig<TNode> config, INodeHashProvider<TNode> nodeHashProvider, Microsoft.Extensions.Logging.ILogger logger)
    {
        _k = config.KSize;
        _b = config.Beta;
        _currentNodeHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        _root = new TreeNode(config.KSize, _currentNodeHash);
        _logger = logger;
        _logger.LogDebug("Initialized KBucketTree with k={KSize}, currentNodeId={CurrentNodeHash}", _k, _currentNodeHash);
    }

    public BucketAddResult TryAddOrRefresh(in ValueHash256 key, TNode node, out TNode? toRefresh)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.LogDebug("Adding node {Node} with XOR distance {Distance}", node, Hash256XorUtils.XorDistance(_currentNodeHash, key));

        TreeNode current = _root;
        // As in, what would be the depth of the node assuming all branch on the traversal is populated.
        int logDistance = Hash256XorUtils.MaxDistance - Hash256XorUtils.CalculateLogDistance(_currentNodeHash, key);
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                _logger.LogTrace("Reached leaf node at depth {Depth}", depth);
                var resp = current.Bucket.TryAddOrRefresh(key, node, out toRefresh);
                if (resp == BucketAddResult.Added)
                {
                    OnNodeAdded?.Invoke(this, node);
                }
                if (resp is BucketAddResult.Added or BucketAddResult.Refreshed)
                {
                    _logger.LogDebug("Successfully added/refreshed node {Node} in bucket at depth {Depth}", node, depth);
                    return resp;
                }

                if (resp == BucketAddResult.Full && ShouldSplit(depth, logDistance))
                {
                    _logger.LogTrace("Splitting bucket at depth {Depth}", depth);
                    SplitBucket(depth, current);
                    continue;
                }

                _logger.LogDebug("Failed to add node {NodeHash} {Node}. Bucket at depth {Depth} is full. {K} {Count}", key, node, depth, _k, current.Bucket.GetAllWithHash().Count());
                return resp;
            }

            bool goRight = GetBit(key, depth);
            _logger.LogTrace("Traversing {Direction} at depth {Depth}", goRight ? "right" : "left", depth);

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    public bool Remove(in ValueHash256 key)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.LogDebug("Attempting to remove node {NodeHash}", key);

        return GetBucketForHash(key).RemoveAndReplace(key);
    }

    public TNode[] GetKNearestNeighbour(ValueHash256 key, ValueHash256 exclude = default, bool excludeSelf = false)
    {
        throw new NotImplementedException();
    }

    public TNode? GetByKey(ValueHash256 key)
    {
        return GetBucketForHash(key).GetByHash(key);
    }

    private KBucket<TNode> GetBucketForHash(ValueHash256 nodeHash)
    {
        TreeNode current = _root;
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                _logger.LogDebug("Reached leaf node at depth {Depth}", depth);
                return current.Bucket;
            }

            bool goRight = GetBit(nodeHash, depth);
            _logger.LogDebug("Traversing {Direction} at depth {Depth}", goRight ? "right" : "left", depth);

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private bool ShouldSplit(int depth, int targetLogDistance)
    {
        bool shouldSplit = depth < 256 && targetLogDistance + _b >= depth;
        _logger.LogDebug("ShouldSplit at depth {Depth}: {ShouldSplit}", depth, shouldSplit);
        return shouldSplit;
    }

    private void SplitBucket(int depth, TreeNode node)
    {
        node.Left = new TreeNode(_k, node.Prefix);
        var rightPrefixBytes = node.Prefix.Bytes.ToArray();
        rightPrefixBytes[depth / 8] |= (byte)(1 << (7 - (depth % 8)));
        node.Right = new TreeNode(_k, new ValueHash256(rightPrefixBytes));

        _logger.LogDebug("Created children at depth {Depth}", depth + 1);

        // The reverse is because the bucket is iterated from the most recent. Without it
        // reading would have reversed this order.
        foreach (var item in node.Bucket.GetAllWithHash().Reverse())
        {
            ValueHash256 itemHash = item.Item1;
            TreeNode? targetNode = GetBit(itemHash, depth) ? node.Right : node.Left;
            targetNode.Bucket.TryAddOrRefresh(itemHash, item.Item2, out _);
            _logger.LogDebug("Moved item {Item} to {Direction} child", item, GetBit(itemHash, depth) ? "right" : "left");
        }

        node.Bucket.Clear();
        _logger.LogDebug("Finished splitting bucket. Left count: {LeftCount}, Right count: {RightCount}", node.Left.Bucket.Count, node.Right.Bucket.Count);
    }

    public TNode[] GetKNearestNeighbour(ValueHash256 key, ValueHash256? exclude = default, bool excludeSelf = false)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        return IterateNeighbour(key)
            .Where(kv => !excludeSelf || kv.Item1 != _currentNodeHash)
            .Where(kv => exclude == null || kv.Item1 != exclude)
            .OrderBy(kv => Hash256XorUtils.XorDistance(kv.Item1, key))
            .Take(_k)
            .Select(kv => kv.Item2)
            .ToArray();
    }

    IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket)> IRoutingTable<TNode, ValueHash256>.IterateBuckets()
    {
        throw new NotImplementedException();
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.LogDebug("Getting all nodes at distance {Distance}", distance);
        List<TNode> result = new List<TNode>();
        GetAllAtDistanceRecursive(_root, 0, distance, result);
        _logger.LogDebug("Found {Count} nodes at distance {Distance}", result.Count, distance);
        return result.ToArray();
    }

    private void GetAllAtDistanceRecursive(TreeNode node, int depth, int distance, List<TNode> result)
    {
        int targetDepth = Hash256XorUtils.MaxDistance - distance;
        if (node.IsLeaf)
        {
            if (depth <= targetDepth)
            {
                result.AddRange(node.Bucket.GetAllWithHash()
                    .Where(kv => Hash256XorUtils.CalculateLogDistance(kv.Item1, _currentNodeHash) == distance)
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

    public IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket)> IterateBuckets()
    {
        using McsLock.Disposable _ = _lock.Acquire();

        // Well, it need to ToArray, otherwise the lock does not really do anything.
        return DoIterateBucketRandomHashes(_root, 0).ToArray();
    }

    private IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket)> DoIterateBucketRandomHashes(TreeNode node, int depth)
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

    private IEnumerable<(ValueHash256, TNode)> IterateNeighbour(ValueHash256 hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        foreach (TreeNode node in IterateNodeFromClosestToTarget(_root, 0, hash))
        {
            foreach ((ValueHash256 nodeHash, TNode item) in node.Bucket.GetAllWithHash())
            {
                yield return (nodeHash, item);
            }
        }
    }

    private IEnumerable<TreeNode> IterateNodeFromClosestToTarget(TreeNode currentNode, int depth, ValueHash256 target)
    {
        if (currentNode.IsLeaf)
        {
            yield return currentNode;
            yield break;
        }

        bool goRight = GetBit(target, depth);
        TreeNode first = goRight ? currentNode.Right! : currentNode.Left!;
        TreeNode second = goRight ? currentNode.Left! : currentNode.Right!;

        foreach (TreeNode node in IterateNodeFromClosestToTarget(first, depth + 1, target))
        {
            yield return node;
        }

        foreach (TreeNode node in IterateNodeFromClosestToTarget(second, depth + 1, target))
        {
            yield return node;
        }
    }

    private bool GetBit(ValueHash256 hash, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = 7 - (index % 8);
        return (hash.Bytes[byteIndex] & (1 << bitIndex)) != 0;
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

        if (node.IsLeaf)
        {
            sb.AppendLine($"Bucket (Depth: {depth}, Count: {node.Bucket.Count})");
        }
        else
        {
            sb.AppendLine($"Node (Depth: {depth})");
            LogTreeStructureRecursive(node.Left!, indent, false, depth + 1, sb);
            LogTreeStructureRecursive(node.Right!, indent, true, depth + 1, sb);
        }
    }

    private void LogTreeStatistics()
    {
        int totalNodes = 0;
        int leafNodes = 0;
        int maxDepth = 0;
        int totalItems = 0;

        void TraverseTree(TreeNode node, int depth)
        {
            totalNodes++;
            maxDepth = Math.Max(maxDepth, depth);

            if (node.IsLeaf)
            {
                leafNodes++;
                totalItems += node.Bucket.Count;
            }
            else
            {
                TraverseTree(node.Left!, depth + 1);
                TraverseTree(node.Right!, depth + 1);
            }
        }

        TraverseTree(_root, 0);

        _logger.LogDebug("Tree Statistics:");
        _logger.LogDebug("Total Nodes: {TotalNodes}", totalNodes);
        _logger.LogDebug("Leaf Nodes: {LeafNodes}", leafNodes);
        _logger.LogDebug("Max Depth: {MaxDepth}", maxDepth);
        _logger.LogDebug("Total Items: {TotalItems}", totalItems);
        _logger.LogDebug("Average Items per Leaf: {AverageItems}", (double)totalItems / leafNodes);
    }

    private void LogTreeStructure()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Tree Structure:");
        LogTreeStructureRecursive(_root, "", true, 0, sb);
        _logger.LogDebug(sb.ToString());
    }

    public void LogDebugInfo()
    {
        LogTreeStructure();
        LogTreeStatistics();
    }

    public event EventHandler<TNode>? OnNodeAdded;

    public int Size => _root.Bucket.Count;
}
