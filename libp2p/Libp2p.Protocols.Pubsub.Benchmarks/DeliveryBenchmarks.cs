// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

/// <summary>
/// Local delivery of one new message when the node is subscribed to many topics and nothing is forwarded.
/// </summary>
[MemoryDiagnoser]
public class DeliveryBenchmarks
{
    private PubsubRouter router = null!;
    private PeerId source = null!;
    private string topic = null!;
    private byte[] payload = null!;
    private long nextId;

    [Params(1, 128)]
    public int Topics { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        router = RouterFixture.Start(RouterFixture.Settings(_ => new MessageId(BitConverter.GetBytes(Interlocked.Increment(ref nextId)))));
        for (int i = 0; i < Topics; i++)
        {
            router.GetTopic($"topic-{i}").OnMessage += static (_, _) => { };
        }

        topic = $"topic-{Topics - 1}";
        Identity sourceIdentity = RouterFixture.Identity(1);
        RouterFixture.Connect(router, sourceIdentity, PubsubRouter.GossipsubProtocolVersionV11, [topic], 1);
        source = sourceIdentity.PeerId;
        payload = new byte[256];
        Random.Shared.NextBytes(payload);
    }

    [GlobalCleanup]
    public void Cleanup() => router.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public Task DeliverMessage()
    {
        Rpc rpc = new();
        rpc.Publish.Add(new Message { Topic = topic, Data = UnsafeByteOperations.UnsafeWrap(payload) });
        return router.OnRpcAsync(source, rpc, PubsubRouter.GossipsubProtocolVersionV11);
    }
}
