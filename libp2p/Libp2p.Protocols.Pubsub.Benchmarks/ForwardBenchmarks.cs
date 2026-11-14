// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

/// <summary>
/// One new message from a mesh peer: validation, local delivery and forwarding to the rest of the mesh.
/// </summary>
[MemoryDiagnoser]
public class ForwardBenchmarks
{
    private const string Topic = "topic";
    private const int MeshPeers = 8;

    private PubsubRouter router = null!;
    private PeerId source = null!;
    private byte[] payload = null!;
    private long nextId;

    [Params(1024, 128 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        router = RouterFixture.Start(RouterFixture.Settings(_ => new MessageId(BitConverter.GetBytes(Interlocked.Increment(ref nextId)))));
        router.GetTopic(Topic).OnMessage += static (_, _) => { };
        for (int i = 1; i <= MeshPeers; i++)
        {
            RouterFixture.Connect(router, RouterFixture.Identity(i), PubsubRouter.GossipsubProtocolVersionV11, [Topic], 1);
        }

        Identity sourceIdentity = RouterFixture.Identity(MeshPeers + 1);
        RouterFixture.Connect(router, sourceIdentity, PubsubRouter.GossipsubProtocolVersionV11, [Topic], 1);
        source = sourceIdentity.PeerId;
        payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
    }

    [GlobalCleanup]
    public void Cleanup() => router.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public async Task ForwardMessage()
    {
        Rpc rpc = new();
        rpc.Publish.Add(new Message { Topic = Topic, Data = UnsafeByteOperations.UnsafeWrap(payload) });
        await router.OnRpcAsync(source, rpc, PubsubRouter.GossipsubProtocolVersionV11);
        await router.FlushOutboundAsync();
    }
}
