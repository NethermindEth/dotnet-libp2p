// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections;
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
    private MessageId lastId;
    private IDictionary firstDeliveries = null!;

    [Params(1, 128)]
    public int Topics { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        router = RouterFixture.Start(RouterFixture.Settings(_ => lastId = new MessageId(BitConverter.GetBytes(++nextId))));
        firstDeliveries = RouterFixture.FirstDeliveries(router);
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
    public async Task DeliverMessage()
    {
        Rpc rpc = new();
        rpc.Publish.Add(new Message { Topic = topic, Data = UnsafeByteOperations.UnsafeWrap(payload) });
        await router.OnRpcAsync(source, rpc, PubsubRouter.GossipsubProtocolVersionV11);
        firstDeliveries.Remove(lastId);
    }
}
