// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Libp2p.Protocols.KadDht.Kademlia;

namespace KadDhtDemo;

internal static class Program
{
    static async Task Main()
    {
        Console.WriteLine("KadDHT .NET sample (core wiring)");
        Console.WriteLine("Note: runtime lookups require McsLock & LruCache to be implemented in the library.");

        ILoggerFactory logManager = NullLoggerFactory.Instance;

        // Use a sample key operator that hashes PublicKey.Bytes via SHA-256 (avoids PublicKey.Hash TODO).
        IKeyOperator<PublicKey, ValueHash256, TestNode> keyOperator = new SampleKeyOperator();
        IKademliaMessageSender<PublicKey, TestNode> transport = new DemoMessageSender();

        KademliaConfig<TestNode> config = new()
        {
            // Minimal current node identity so routing can initialize
            CurrentNodeId = new TestNode { Id = RandomPublicKey() },
            KSize = 16,
            Alpha = 3,
            Beta = 2,
        };

        INodeHashProvider<ValueHash256, TestNode> nodeHashProvider = new FromKeyNodeHashProvider<PublicKey, ValueHash256, TestNode>(keyOperator);
        IRoutingTable<ValueHash256, TestNode> routingTable = new KBucketTree<ValueHash256, TestNode>(config, nodeHashProvider, logManager);
    INodeHealthTracker<TestNode> nodeHealthTracker = new NodeHealthTracker<PublicKey, ValueHash256, TestNode>(config, routingTable, nodeHashProvider, transport, logManager);
        ILookupAlgo<ValueHash256, TestNode> lookupAlgo = new LookupKNearestNeighbour<ValueHash256, TestNode>(routingTable, nodeHashProvider, nodeHealthTracker, config, logManager);

    var kad = new Kademlia<PublicKey, ValueHash256, TestNode>(keyOperator, transport, routingTable, lookupAlgo, logManager, nodeHealthTracker, config);

        Console.WriteLine("Kademlia core components constructed.");

        // Example: Run bootstrap and a lookup (commented out until library TODOs are completed).
        // await kad.Bootstrap(CancellationToken.None);
        // PublicKey target = RandomPublicKey();
        // var nodes = await lookupAlgo.Lookup(
        //     keyOperator.GetKeyHash(target),
        //     config.KSize,
        //     async (nextNode, token) =>
        //     {
        //         // If querying self, return local K-nearest only
        //         if (keyOperator.GetNodeHash(nextNode).Equals(keyOperator.GetKeyHash(config.CurrentNodeId.Id)))
        //         {
        //             var keyHash = keyOperator.GetKeyHash(target);
        //             return routingTable.GetKNearestNeighbour(keyHash);
        //         }
        //         return await transport.FindNeighbours(nextNode, target, token);
        //     },
        //     CancellationToken.None);
        // Console.WriteLine($"Lookup returned {nodes.Length} nodes");
    }

    private static PublicKey RandomPublicKey()
    {
        Span<byte> randomBytes = stackalloc byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}

internal sealed class DemoMessageSender : IKademliaMessageSender<PublicKey, TestNode>
{
    public Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token)
    {
        // Transport mock: return empty result
        return Task.FromResult(Array.Empty<TestNode>());
    }

    public Task Ping(TestNode receiver, CancellationToken token)
    {
        // Transport mock: no-op
        return Task.CompletedTask;
    }
}

internal sealed class SampleKeyOperator : IKeyOperator<PublicKey, ValueHash256, TestNode>
{
    public PublicKey GetKey(TestNode node) => node.Id;

    public ValueHash256 GetKeyHash(PublicKey key)
    {
        // Hash the raw public key bytes (placeholder hashing for the sample)
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(key.Bytes.ToArray());
        return ValueHash256.FromBytes(hash);
    }

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        // Simple random key; proper implementation should honor distance using Hash256XorUtils
        Span<byte> randomBytes = stackalloc byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}