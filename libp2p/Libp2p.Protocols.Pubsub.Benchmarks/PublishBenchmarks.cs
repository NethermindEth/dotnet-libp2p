// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

/// <summary>
/// A local publish that floods every gossipsub peer of the topic, with and without message signing.
/// </summary>
[MemoryDiagnoser]
public class PublishBenchmarks
{
    private const string Topic = "topic";
    private const int Peers = 8;

    private PubsubRouter router = null!;
    private byte[] payload = null!;
    private long nextId;

    [Params(1024, 128 * 1024)]
    public int PayloadSize { get; set; }

    [Params(false, true)]
    public bool Signed { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        router = RouterFixture.Start(Signed
            ? RouterFixture.Settings(sign: true)
            : RouterFixture.Settings(_ => new MessageId(BitConverter.GetBytes(Interlocked.Increment(ref nextId)))));
        router.GetTopic(Topic);
        for (int i = 1; i <= Peers; i++)
        {
            RouterFixture.Connect(router, RouterFixture.Identity(i), PubsubRouter.GossipsubProtocolVersionV11, [Topic], 1);
        }

        payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
    }

    [GlobalCleanup]
    public void Cleanup() => router.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public Task Publish()
    {
        router.Publish(Topic, payload);
        return router.FlushOutboundAsync();
    }
}
