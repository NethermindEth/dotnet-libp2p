// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class MeshBackoffE2eTests
{
    [TestCase(PubsubRouter.GossipsubProtocolVersionV10, false)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11, false)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12, false)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13, false)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV10, true)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11, true)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12, true)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13, true)]
    public async Task RegisteredListenerEnforcesMeshScoreAndSafeBackoff(string protocol, bool negativeGraft)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        Clock clock = new();
        await using ServiceProvider receiverServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub())
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(new PubsubSettings
            {
                AutoConnect = false,
                HeartbeatInterval = int.MaxValue,
                DecayInterval = int.MaxValue,
                DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
                GetMessageId = message => new MessageId(message.Data.ToByteArray()),
                AppSpecificWeight = 1,
            }).BuildServiceProvider();
        WirePeer wire = new(protocol);
        await using ServiceProvider senderServices = new ServiceCollection()
            .AddLibp2p(builder => builder.AddProtocol(wire)).BuildServiceProvider();
        await using ILocalPeer receiver = receiverServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer sender = senderServices.GetRequiredService<IPeerFactory>().Create();
        await receiver.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        PubsubRouter router = receiverServices.GetRequiredService<PubsubRouter>();
        await router.StartAsync(receiver, deadline.Token);
        router.GetTopic("topic");
        router.GetTopic("second");
        router.GetTopic("marker");
        ConcurrentDictionary<byte, TaskCompletionSource> markers = new();
        router.OnMessage += (_, peer, data) =>
        {
            Assert.That(peer, Is.EqualTo(sender.Identity.PeerId));
            if (markers.TryGetValue(data[0], out TaskCompletionSource? processed)) processed.TrySetResult();
        };
        ISession session = await sender.DialAsync(receiver.ListenAddresses.First(), deadline.Token);
        Assert.That(session.RemotePeerId, Is.EqualTo(receiver.Identity.PeerId));
        Task stream = session.DialAsync(wire, deadline.Token);
        try
        {
            IChannel channel = await wire.Ready.Task.WaitAsync(deadline.Token);
            // The reply snapshot proves negotiation selected this registered production handler.
            Rpc snapshot = await wire.Replies.Reader.ReadAsync(deadline.Token);
            Assert.That(snapshot.Subscriptions.Select(sub => sub.Topicid), Is.EquivalentTo(new[] { "topic", "second", "marker" }));
            async Task Marker(byte id)
            {
                TaskCompletionSource processed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                markers[id] = processed;
                await channel.WriteSizeAndProtobufAsync(new Rpc { Publish = { new Message { Topic = "marker", Data = ByteString.CopyFrom([id]) } } });
                await processed.Task.WaitAsync(deadline.Token);
            }
            await Marker(1);
            var state = (IRoutingStateContainer)router;
            if (negativeGraft)
            {
                // The first marker earned 0.5 first-delivery credit. Keep the total exactly -10.
                router.SetAppSpecificScore(sender.Identity.PeerId, -10.5);
                Assert.That((double)typeof(PubsubRouter).GetMethod("GetPeerScore", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(router, [sender.Identity.PeerId])!, Is.EqualTo(-10));
                await channel.WriteSizeAndProtobufAsync(new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "topic" } } } });
                await Marker(2);
                lock (router) Assert.That(state.Mesh["topic"], Does.Not.Contain(sender.Identity.PeerId));
                Rpc response = await wire.Replies.Reader.ReadAsync(deadline.Token);
                ControlPrune prune = response.Control.Prune.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(prune.TopicID, Is.EqualTo("topic"));
                    Assert.That(prune.Peers, Is.Empty);
                    Assert.That(prune.HasBackoff, Is.EqualTo(protocol != PubsubRouter.GossipsubProtocolVersionV10));
                    Assert.That(Backoff(router, sender.Identity.PeerId, "topic"), Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
                });
            }
            else
            {
                await channel.WriteSizeAndProtobufAsync(new Rpc
                {
                    Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "topic" }, new ControlGraft { TopicID = "second" } } },
                });
                await Marker(2);
                lock (router)
                {
                    Assert.That(state.Mesh["topic"], Has.Member(sender.Identity.PeerId));
                    Assert.That(state.Mesh["second"], Has.Member(sender.Identity.PeerId));
                }
                await channel.WriteSizeAndProtobufAsync(new Rpc
                {
                    Control = new ControlMessage
                    {
                        Prune = { new ControlPrune { TopicID = "topic", Backoff = ulong.MaxValue }, new ControlPrune { TopicID = "second", Backoff = 30 } },
                        Ihave = { new ControlIHave { TopicID = "topic", MessageIDs = { ByteString.CopyFrom([9]) } } },
                    },
                });
                await Marker(3);
                lock (router)
                {
                    Assert.That(state.Mesh["topic"], Is.Empty);
                    Assert.That(state.Mesh["second"], Is.Empty);
                }
                Assert.That(Backoff(router, sender.Identity.PeerId, "topic"), Is.EqualTo(clock.Now.UtcDateTime.AddHours(24)));
                Rpc response = await wire.Replies.Reader.ReadAsync(deadline.Token);
                Assert.That(response.Control.Iwant.Single().MessageIDs, Is.EqualTo(new[] { ByteString.CopyFrom([9]) }));
            }
        }
        finally
        {
            await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await router.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            try { await stream.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stream.IsCanceled) { }
        }
    }

    private static DateTime Backoff(PubsubRouter router, PeerId peerId, string topic)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        lock (router)
        {
            object peers = typeof(PubsubRouter).GetField("peerState", flags)!.GetValue(router)!;
            object peer = peers.GetType().GetProperty("Item")!.GetValue(peers, [peerId])!;
            return ((Dictionary<string, DateTime>)peer.GetType().GetProperty("Backoff")!.GetValue(peer)!)[topic];
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WirePeer(string id) : ISessionProtocol
    {
        public string Id => id;
        public TaskCompletionSource<IChannel> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<Rpc> Replies { get; } = System.Threading.Channels.Channel.CreateUnbounded<Rpc>();
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            Ready.TrySetResult(channel);
            await channel;
        }
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            try
            {
                while (!channel.CancellationToken.IsCancellationRequested)
                    Replies.Writer.TryWrite(await channel.ReadPrefixedProtobufAsync(Rpc.Parser, 1024 * 1024, channel.CancellationToken));
            }
            catch (OperationCanceledException) when (channel.CancellationToken.IsCancellationRequested) { }
            catch (Nethermind.Libp2p.Core.Exceptions.ChannelClosedException) { }
        }
    }
}
