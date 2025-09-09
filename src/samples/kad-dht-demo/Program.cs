using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core;
using TestNode = Libp2p.Protocols.KadDht.TestNode;

namespace KadDhtDemo
{
    /// <summary>
    /// Extension methods for type conversions in the demo.
    /// </summary>
    internal static class DemoTypeExtensions
    {
        public static PeerId ToPeerId(this PublicKey publicKey)
        {
            return new PeerId(publicKey.Bytes.ToArray());
        }

        public static PublicKey ToKademliaKey(this PeerId peerId)
        {
            return new PublicKey(peerId.Bytes);
        }
    }

    namespace KadDhtDemo
    {
        internal static class Program
        {
            public static async Task Main(string[] args)
            {
                using ILoggerFactory logManager = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                {
                    builder
                        .SetMinimumLevel(LogLevel.Debug)
                        .AddSimpleConsole(o =>
                        {
                            o.SingleLine = true;
                            o.TimestampFormat = "HH:mm:ss.fff ";
                        });
                    // Simple file logging (basic) - append raw lines
                    builder.AddProvider(new SimpleFileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "kad-demo.log")));
                });

                IKeyOperator<PublicKey, ValueHash256, TestNode> keyOperator = new PublicKeyKeyOperator();
                Libp2p.Protocols.KadDht.Kademlia.IKademliaMessageSender<PublicKey, TestNode> transport = new DemoMessageSender(logManager);

                // add bootstrap nodes
                var bootstrapNodes = new List<TestNode>();
                for (int i = 0; i < 3; i++)
                {
                    bootstrapNodes.Add(new TestNode(RandomPublicKey().ToPeerId()));
                }

                KademliaConfig<TestNode> config = new()
                {
                    CurrentNodeId = new TestNode(RandomPublicKey().ToPeerId()),
                    KSize = 16,
                    Alpha = 3,
                    Beta = 2,
                    RefreshInterval = TimeSpan.FromMinutes(1), // Shorter interval for demo purposes
                    LookupFindNeighbourHardTimout = TimeSpan.FromSeconds(5),
                    RefreshPingTimeout = TimeSpan.FromSeconds(2),
                    NodeRequestFailureThreshold = 3,
                    BootNodes = bootstrapNodes
                };

                INodeHashProvider<ValueHash256, TestNode> nodeHashProvider = new FromKeyNodeHashProvider<PublicKey, ValueHash256, TestNode>(keyOperator);
                IRoutingTable<ValueHash256, TestNode> routingTable = new KBucketTree<ValueHash256, TestNode>(config, nodeHashProvider, logManager);
                INodeHealthTracker<TestNode> nodeHealthTracker = new NodeHealthTracker<PublicKey, ValueHash256, TestNode>(config, routingTable, nodeHashProvider, transport, logManager);
                ILookupAlgo<ValueHash256, TestNode> lookupAlgo = new LookupKNearestNeighbour<ValueHash256, TestNode>(routingTable, nodeHashProvider, nodeHealthTracker, config, logManager);

                var kad = new Kademlia<PublicKey, ValueHash256, TestNode>(keyOperator, transport, routingTable, lookupAlgo, logManager, nodeHealthTracker, config);

                // Subscribe to node addition events
                kad.OnNodeAdded += (sender, node) =>
                {
                    Console.WriteLine($"Node added to routing table: {node.Id}");
                };

                Console.WriteLine("Kademlia demo starting...");
                Console.WriteLine($"Current node ID: {config.CurrentNodeId.Id}");
                Console.WriteLine($"Config - K: {config.KSize}, Alpha: {config.Alpha}, Beta: {config.Beta}");
                Console.WriteLine($"Bootstrap nodes: {config.BootNodes.Count}");

                // Distance-diverse deterministic seeding to encourage bucket splits
                Console.WriteLine("\n1. Seeding routing table with distance-diverse nodes...");
                SeedDeterministic(config.CurrentNodeId.Id.ToKademliaKey().Hash, nodeHealthTracker, maxDistance: 14, perDistance: 4);

                // Show routing table statistics
                Console.WriteLine($"\n2. Routing table size after seeding: {routingTable.Size}");
                routingTable.LogDebugInfo();

                // Test local routing table operations
                Console.WriteLine("\n3. Testing local routing table operations...");
                var randomTarget = RandomPublicKey();
                try
                {
                    var nearestNodes = kad.GetKNeighbour(randomTarget, excludeSelf: true);
                    Console.WriteLine($"Found {nearestNodes.Length} nearest neighbors for target {randomTarget.ToPeerId()}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in GetKNeighbour: {ex.Message}");
                }

                Console.WriteLine("\n4. Testing distance-based queries...");
                try
                {
                    for (int distance = 1; distance <= 5; distance++)
                    {
                        var nodesAtDistance = kad.GetAllAtDistance(distance);
                        Console.WriteLine($"Nodes at distance {distance}: {nodesAtDistance.Length}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in distance queries: {ex.Message}");
                }

                Console.WriteLine("\n5. Iterating all nodes in routing table...");
                try
                {
                    int totalNodes = kad.IterateNodes().Count();
                    Console.WriteLine($"Total nodes via iteration: {totalNodes}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in node iteration: {ex.Message}");
                }

                // Run network lookups
                Console.WriteLine("\n6. Running network lookup operations...");
                var lookupTarget1 = RandomPublicKey();
                var lookupResult1 = await kad.LookupNodesClosest(lookupTarget1, CancellationToken.None);
                Console.WriteLine($"Lookup 1 found {lookupResult1.Length} nodes for target {lookupTarget1.ToPeerId()}");

                var lookupTarget2 = RandomPublicKey();
                var lookupResult2 = await kad.LookupNodesClosest(lookupTarget2, CancellationToken.None, k: 8); // Custom K value
                Console.WriteLine($"Lookup 2 (k=8) found {lookupResult2.Length} nodes for target {lookupTarget2.ToPeerId()}");

                Console.WriteLine("\n7. Running bootstrap operation...");
                await kad.Bootstrap(CancellationToken.None);

                Console.WriteLine("\n8. Testing manual node management...");
                var newNode = new TestNode(RandomPublicKey().ToPeerId());
                kad.AddOrRefresh(newNode);
                Console.WriteLine($"Manually added node: {newNode.Id}");

                Console.WriteLine($"\n9. Final routing table size: {routingTable.Size}");
                routingTable.LogDebugInfo();

                Console.WriteLine("\n10. Network simulation statistics:");
                ((DemoMessageSender)transport).LogNetworkStats();

                Console.WriteLine("\nDemo complete! All Kademlia components exercised.");
            }

            internal static PublicKey RandomPublicKey()
            {
                Span<byte> randomBytes = stackalloc byte[32]; // Use 32 bytes for consistent key size
                Random.Shared.NextBytes(randomBytes);
                return new PublicKey(randomBytes);
            }

            private static void SeedDeterministic(ValueHash256 baseHash, INodeHealthTracker<TestNode> tracker, int maxDistance, int perDistance)
            {
                for (int d = 1; d <= maxDistance; d++)
                {
                    for (int i = 0; i < perDistance; i++)
                    {
                        var targetHash = ValueHash256.GetRandomHashAtDistance(baseHash, d);
                        var pk = PublicKey.FromHash(targetHash);
                        tracker.OnIncomingMessageFrom(new TestNode(pk.ToPeerId()));
                    }
                }
            }
        }

        /// <summary>
        /// Demo transport that can be swapped for a real libp2p-backed sender.
        /// Uses in-memory simulation for benchmarks, but preserves async flow and timeouts.
        /// </summary>
        internal sealed class DemoMessageSender : Libp2p.Protocols.KadDht.Kademlia.IKademliaMessageSender<PublicKey, TestNode>
        {
            private readonly ILogger<DemoMessageSender> _log;
            private readonly TimeSpan _simulatedLatency;
            private readonly Random _rng = new();
            private readonly Dictionary<TestNode, DateTime> _lastSeen = new();

            public DemoMessageSender(ILoggerFactory? loggerFactory = null, TimeSpan? simulatedLatency = null)
            {
                _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DemoMessageSender>();
                _simulatedLatency = simulatedLatency ?? TimeSpan.FromMilliseconds(Random.Shared.Next(5, 50)); // Variable latency
            }

            public async Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token)
            {
                // Simulate network latency with some variance
                var latency = TimeSpan.FromMilliseconds(_simulatedLatency.TotalMilliseconds + _rng.Next(-10, 20));
                await Task.Delay(latency, token).ConfigureAwait(false);

                // Track when we last saw this node
                _lastSeen[receiver] = DateTime.UtcNow;

                // Simulate occasional network failures (5% chance)
                if (_rng.NextDouble() < 0.05)
                {
                    throw new TimeoutException($"Simulated network timeout to {receiver.Id}");
                }


                // For simulation, return a mix of random nodes and nodes "closer" to target
                int count = _rng.Next(1, 6); // Return 1-5 nodes
                var nodes = new TestNode[count];

                for (int i = 0; i < count; i++)
                {
                    // 70% chance to return a completely random node
                    // 30% chance to return a node that's "closer" to target (simulation)
                    if (_rng.NextDouble() < 0.7)
                    {
                        nodes[i] = new TestNode(Program.RandomPublicKey().ToPeerId());
                    }
                    else
                    {
                        // Generate a node that's "closer" to target for better lookup convergence
                        var targetHash = target.Hash;
                        var closerKey = PublicKey.FromHash(ValueHash256.GetRandomHashAtDistance(targetHash, _rng.Next(1, 8)));
                        nodes[i] = new TestNode(closerKey.ToPeerId());
                    }
                }

                _log.LogDebug("Simulated FindNeighbours to {Receiver}: returned {Count} nodes (latency: {Latency}ms)",
                    receiver.Id, count, latency.TotalMilliseconds);
                return nodes;
            }

            public async Task Ping(TestNode receiver, CancellationToken token)
            {
                // Simulate network latency
                var latency = TimeSpan.FromMilliseconds(_simulatedLatency.TotalMilliseconds + _rng.Next(-5, 10));
                await Task.Delay(latency, token).ConfigureAwait(false);

                // Track when we last saw this node
                _lastSeen[receiver] = DateTime.UtcNow;

                // Simulate occasional ping failures (3% chance)
                if (_rng.NextDouble() < 0.03)
                {
                    throw new TimeoutException($"Simulated ping timeout to {receiver.Id}");
                }

                _log.LogDebug("Simulated Ping to {Receiver} (latency: {Latency}ms)", receiver.Id, latency.TotalMilliseconds);
            }

            /// <summary>
            /// Get statistics about the simulated network
            /// </summary>
            public void LogNetworkStats()
            {
                var recentNodes = _lastSeen.Where(kvp => DateTime.UtcNow - kvp.Value < TimeSpan.FromMinutes(5)).Count();
                _log.LogInformation("Network simulation stats: {RecentNodes} nodes contacted in last 5 minutes", recentNodes);
            }
        }
    }
}
