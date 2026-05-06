// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Libp2p.Protocols.KadDht.Tests;

[TestFixture]
public class DisjointPathLookupTests
{
    private ILoggerFactory _loggerFactory = null!;

    [SetUp]
    public void Setup()
    {
        _loggerFactory = NullLoggerFactory.Instance;
    }

    [Test]
    public void Constructor_ThrowsForPathsLessThanTwo()
    {
        var inner = new FakeLookupAlgo();
        var hashProvider = new FakeNodeHashProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DisjointPathLookup<ValueHash256, FakeNode>(inner, hashProvider, 1, _loggerFactory));
    }

    [Test]
    public async Task Lookup_MergesResultsFromMultiplePaths()
    {
        // Each path returns different nodes
        var node1 = CreateNode(1);
        var node2 = CreateNode(2);
        var node3 = CreateNode(3);

        int callCount = 0;
        var inner = new FakeLookupAlgo((_target, _k, _findOp, _token) =>
        {
            int path = Interlocked.Increment(ref callCount);
            return Task.FromResult(path switch
            {
                1 => new[] { node1, node2 },
                2 => new[] { node2, node3 },
                _ => new[] { node1 }
            });
        });

        var hashProvider = new FakeNodeHashProvider();
        var lookup = new DisjointPathLookup<ValueHash256, FakeNode>(inner, hashProvider, 2, _loggerFactory);

        var targetHash = ValueHash256.FromBytes(new byte[32]);
        var result = await lookup.Lookup(targetHash, 10, (_, _) => Task.FromResult<FakeNode[]?>(null), CancellationToken.None);

        // Should have all 3 unique nodes merged from both paths
        Assert.That(result, Has.Length.EqualTo(3));
    }

    [Test]
    public async Task Lookup_RespectsKLimit()
    {
        var nodes = Enumerable.Range(1, 10).Select(i => CreateNode(i)).ToArray();

        var inner = new FakeLookupAlgo((_, _, _, _) =>
            Task.FromResult(nodes));

        var hashProvider = new FakeNodeHashProvider();
        var lookup = new DisjointPathLookup<ValueHash256, FakeNode>(inner, hashProvider, 2, _loggerFactory);

        var targetHash = ValueHash256.FromBytes(new byte[32]);
        var result = await lookup.Lookup(targetHash, 3, (_, _) => Task.FromResult<FakeNode[]?>(null), CancellationToken.None);

        Assert.That(result, Has.Length.EqualTo(3));
    }

    [Test]
    public async Task Lookup_HandlesPathFailureGracefully()
    {
        var node1 = CreateNode(1);
        int callCount = 0;

        var inner = new FakeLookupAlgo((_, _, _, _) =>
        {
            int path = Interlocked.Increment(ref callCount);
            if (path == 2) throw new Exception("Path 2 failed");
            return Task.FromResult(new[] { node1 });
        });

        var hashProvider = new FakeNodeHashProvider();
        var lookup = new DisjointPathLookup<ValueHash256, FakeNode>(inner, hashProvider, 2, _loggerFactory);

        var targetHash = ValueHash256.FromBytes(new byte[32]);
        var result = await lookup.Lookup(targetHash, 10, (_, _) => Task.FromResult<FakeNode[]?>(null), CancellationToken.None);

        // Should still get results from path 1
        Assert.That(result, Has.Length.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task Lookup_DeduplicatesNodesAcrossPaths()
    {
        var sharedNode = CreateNode(42);

        var inner = new FakeLookupAlgo((_, _, _, _) =>
            Task.FromResult(new[] { sharedNode }));

        var hashProvider = new FakeNodeHashProvider();
        var lookup = new DisjointPathLookup<ValueHash256, FakeNode>(inner, hashProvider, 3, _loggerFactory);

        var targetHash = ValueHash256.FromBytes(new byte[32]);
        var result = await lookup.Lookup(targetHash, 10, (_, _) => Task.FromResult<FakeNode[]?>(null), CancellationToken.None);

        // Same node returned by 3 paths — should deduplicate to 1
        Assert.That(result, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Lookup_DisjointPaths_PreventNodeReuse()
    {
        // This tests the global seen set: the findNeighbourOp should not be called
        // for the same node by different paths

        var queriedNodes = new System.Collections.Concurrent.ConcurrentBag<int>();

        var node1 = CreateNode(1);
        var node2 = CreateNode(2);

        // The inner lookup forwards to our findNeighbourOp
        var inner = new FakeLookupAlgo(async (target, k, findOp, token) =>
        {
            var results = new List<FakeNode>();
            foreach (var seed in new[] { node1, node2 })
            {
                var r = await findOp(seed, token);
                if (r != null) results.AddRange(r);
            }
            return results.ToArray();
        });

        var hashProvider = new FakeNodeHashProvider();
        var lookup = new DisjointPathLookup<ValueHash256, FakeNode>(inner, hashProvider, 2, _loggerFactory);

        var targetHash = ValueHash256.FromBytes(new byte[32]);
        await lookup.Lookup(targetHash, 10, (node, _) =>
        {
            queriedNodes.Add(node.Id);
            return Task.FromResult<FakeNode[]?>(Array.Empty<FakeNode>());
        }, CancellationToken.None);

        // Each node should appear at most once across all paths (disjoint guarantee)
        var grouped = queriedNodes.GroupBy(id => id);
        foreach (var group in grouped)
        {
            Assert.That(group.Count(), Is.EqualTo(1),
                $"Node {group.Key} was queried {group.Count()} times — disjoint paths should prevent reuse");
        }
    }

    #region Test Helpers

    private static FakeNode CreateNode(int id)
    {
        var bytes = new byte[32];
        bytes[0] = (byte)(id & 0xFF);
        bytes[1] = (byte)((id >> 8) & 0xFF);
        return new FakeNode(id, ValueHash256.FromBytes(bytes));
    }

    public sealed class FakeNode : IComparable<FakeNode>
    {
        public int Id { get; }
        public ValueHash256 Hash { get; }

        public FakeNode(int id, ValueHash256 hash) { Id = id; Hash = hash; }

        public int CompareTo(FakeNode? other) => Id.CompareTo(other?.Id ?? 0);

        public override bool Equals(object? obj) => obj is FakeNode fn && fn.Id == Id;
        public override int GetHashCode() => Id;
    }

    private sealed class FakeNodeHashProvider : INodeHashProvider<ValueHash256, FakeNode>
    {
        public ValueHash256 GetHash(FakeNode node) => node.Hash;
    }

    private sealed class FakeLookupAlgo : ILookupAlgo<ValueHash256, FakeNode>
    {
        private readonly Func<ValueHash256, int, Func<FakeNode, CancellationToken, Task<FakeNode[]?>>, CancellationToken, Task<FakeNode[]>> _impl;

        public FakeLookupAlgo(
            Func<ValueHash256, int, Func<FakeNode, CancellationToken, Task<FakeNode[]?>>, CancellationToken, Task<FakeNode[]>>? impl = null)
        {
            _impl = impl ?? ((_, _, _, _) => Task.FromResult(Array.Empty<FakeNode>()));
        }

        public Task<FakeNode[]> Lookup(ValueHash256 targetHash, int k,
            Func<FakeNode, CancellationToken, Task<FakeNode[]?>> findNeighbourOp, CancellationToken token) =>
            _impl(targetHash, k, findNeighbourOp, token);
    }

    #endregion
}
