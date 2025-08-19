using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Libp2p.Protocols.KadDht.Kademlia;

namespace KadDhtDemo
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            using ILoggerFactory logManager = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Debug)
                    .AddSimpleConsole(o =>
                    {
                        o.SingleLine = true;
                        o.TimestampFormat = "HH:mm:ss.fff ";
                    });
            });

            IKeyOperator<PublicKey, ValueHash256, TestNode> keyOperator = new PublicKeyKeyOperator();
            IKademliaMessageSender<PublicKey, TestNode> transport = new DemoMessageSender(logManager);

            KademliaConfig<TestNode> config = new()
            {
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

            Console.WriteLine("Kademlia demo starting. Seeding table and running one lookup...");

            // Seed some nodes locally so the routing table isn't empty
            for (int i = 0; i < 64; i++)
            {
                nodeHealthTracker.OnIncomingMessageFrom(new TestNode { Id = RandomPublicKey() });
            }

            // Run a single lookup to exercise the path
            _ = await kad.LookupNodesClosest(RandomPublicKey(), CancellationToken.None);
            Console.WriteLine("Lookup complete.");
        }

        internal static PublicKey RandomPublicKey()
        {
            Span<byte> randomBytes = stackalloc byte[64];
            Random.Shared.NextBytes(randomBytes);
            return new PublicKey(randomBytes);
        }
    }

    /// <summary>
    /// Demo transport that can be swapped for a real libp2p-backed sender.
    /// Uses in-memory simulation for benchmarks, but preserves async flow and timeouts.
    /// </summary>
    internal sealed class DemoMessageSender : IKademliaMessageSender<PublicKey, TestNode>
    {
        private readonly ILogger<DemoMessageSender> _log;
        private readonly TimeSpan _simulatedLatency;
        private readonly Random _rng = new();

        public DemoMessageSender(ILoggerFactory? loggerFactory = null, TimeSpan? simulatedLatency = null)
        {
            _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DemoMessageSender>();
            _simulatedLatency = simulatedLatency ?? TimeSpan.FromMilliseconds(5);
        }

        public async Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token)
        {
            // Simulate network latency
            await Task.Delay(_simulatedLatency, token).ConfigureAwait(false);

            // In a real transport, you’d:
            // 1. Dial the peer (receiver) over libp2p
            // 2. Send a FindNeighboursRequest (target key + K)
            // 3. Read and decode the FindNeighboursResponse
            // For now, return a small random set for testing
            int count = _rng.Next(0, 4);
            var nodes = new TestNode[count];
            for (int i = 0; i < count; i++)
            {
                nodes[i] = new TestNode { Id = Program.RandomPublicKey() };
            }

            _log.LogDebug("Simulated FindNeighbours to {Receiver}: returned {Count} nodes", receiver, count);
            return nodes;
        }

        public async Task Ping(TestNode receiver, CancellationToken token)
        {
            // Simulate network latency
            await Task.Delay(_simulatedLatency, token).ConfigureAwait(false);

            // In a real transport, you’d:
            // 1. Dial the peer over libp2p
            // 2. Send a PingRequest
            // 3. Await a PingResponse
            _log.LogDebug("Simulated Ping to {Receiver}", receiver);
        }
    }
}
