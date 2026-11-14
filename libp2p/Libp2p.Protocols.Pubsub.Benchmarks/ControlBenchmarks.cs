// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

/// <summary>
/// Control-only RPCs from a gossipsub v1.2 mesh peer. The peer's per-heartbeat control
/// counters are reset before each RPC, as a heartbeat would, so every RPC is processed.
/// </summary>
[MemoryDiagnoser]
public class ControlBenchmarks
{
    private const string Topic = "topic";
    private const int IdLength = 20;
    private const int Ids = 5_000;

    private PubsubRouter router = null!;
    private PubsubRouter.PubsubPeer peer = null!;
    private PeerId peerId = null!;
    private Rpc ihaveKnown = null!;
    private Rpc iwantKnown = null!;
    private Rpc iwantUnknown = null!;
    private Rpc idontwant = null!;

    [GlobalSetup]
    public void Setup()
    {
        router = RouterFixture.Start(RouterFixture.Settings(static message => new MessageId(message.Data.ToByteArray())));
        router.GetTopic(Topic);
        Identity identity = RouterFixture.Identity(1);
        peer = RouterFixture.Connect(router, identity, PubsubRouter.GossipsubProtocolVersionV12, [Topic], 1);
        peerId = identity.PeerId;

        // The data of each message is its ID, so the router has seen exactly these IDs.
        Identity sourceIdentity = RouterFixture.Identity(2);
        RouterFixture.Connect(router, sourceIdentity, PubsubRouter.GossipsubProtocolVersionV12, [Topic], 1);
        ControlIHave ihave = new() { TopicID = Topic };
        ControlIWant iwant = new();
        ControlIWant iwantCached = new();
        for (int i = 0; i < Ids; i++)
        {
            ByteString known = Id(i);
            Rpc message = new();
            message.Publish.Add(new Message { Topic = Topic, Data = known });
            router.OnRpcAsync(sourceIdentity.PeerId, message, PubsubRouter.GossipsubProtocolVersionV12).GetAwaiter().GetResult();
            ihave.MessageIDs.Add(known);
            iwantCached.MessageIDs.Add(known);
            iwant.MessageIDs.Add(Id(Ids + i));
        }
        router.FlushOutboundAsync().GetAwaiter().GetResult();

        ihaveKnown = new Rpc { Control = new ControlMessage { Ihave = { ihave } } };
        iwantKnown = new Rpc { Control = new ControlMessage { Iwant = { iwantCached } } };
        iwantUnknown = new Rpc { Control = new ControlMessage { Iwant = { iwant } } };
        idontwant = new Rpc { Control = new ControlMessage { Idontwant = { new ControlIDontWant { MessageIDs = { Id(0) } } } } };
    }

    private static ByteString Id(int index)
    {
        byte[] id = new byte[IdLength];
        BinaryPrimitives.WriteInt32BigEndian(id, index);
        return ByteString.CopyFrom(id);
    }

    [GlobalCleanup]
    public void Cleanup() => router.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public Task IhaveKnownIds()
    {
        peer.Control.ResetHeartbeatCounters(0);
        return router.OnRpcAsync(peerId, ihaveKnown, PubsubRouter.GossipsubProtocolVersionV12);
    }

    // After the retransmission limit is reached for every ID, the RPC only looks the IDs up.
    [Benchmark]
    public Task IwantKnownIds()
    {
        peer.Control.ResetHeartbeatCounters(0);
        return router.OnRpcAsync(peerId, iwantKnown, PubsubRouter.GossipsubProtocolVersionV12);
    }

    [Benchmark]
    public Task IwantUnknownIds()
    {
        peer.Control.ResetHeartbeatCounters(0);
        return router.OnRpcAsync(peerId, iwantUnknown, PubsubRouter.GossipsubProtocolVersionV12);
    }

    [Benchmark]
    public Task IdontwantOneId()
    {
        peer.Control.ResetHeartbeatCounters(0);
        return router.OnRpcAsync(peerId, idontwant, PubsubRouter.GossipsubProtocolVersionV12);
    }
}
