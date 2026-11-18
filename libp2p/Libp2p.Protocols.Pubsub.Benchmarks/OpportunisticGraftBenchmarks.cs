// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

[MemoryDiagnoser]
public class OpportunisticGraftBenchmarks
{
    private const int ConnectedPeers = 512;
    private const int MeshSize = 2;

    private PubsubRouter router = null!;

    [Params(false, true)]
    public bool OverlappingTopics { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string[] topics = Enumerable.Range(0, OverlappingTopics ? 8 : 1).Select(i => $"topic-{i}").ToArray();
        PubsubSettings settings = RouterFixture.Settings(sign: true);
        settings.Degree = MeshSize;
        settings.LowestDegree = MeshSize;
        settings.HighestDegree = 4;
        settings.DOut = 0;
        settings.OpportunisticGraftTicks = 1;
        settings.OpportunisticGraftThreshold = OverlappingTopics ? 1 : -1;
        router = RouterFixture.Start(settings);
        foreach (string topic in topics)
        {
            router.GetTopic(topic);
        }

        // Every score is zero. The sparse case has a healthy mesh and the overlapping
        // case scans every subscriber without finding a better peer, keeping the meshes stable.
        for (int i = 1; i <= ConnectedPeers; i++)
        {
            bool inMesh = i <= MeshSize;
            string[] subscriptions = inMesh || OverlappingTopics ? topics : ["other"];
            RouterFixture.Connect(router, RouterFixture.Identity(i), PubsubRouter.GossipsubProtocolVersionV12,
                subscriptions, inMesh ? topics.Length : 0);
        }
    }

    [Benchmark]
    public Task Heartbeat() => router.Heartbeat();

    [GlobalCleanup]
    public void Cleanup() => router.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
