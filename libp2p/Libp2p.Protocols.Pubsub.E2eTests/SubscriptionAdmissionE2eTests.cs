// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class SubscriptionAdmissionE2eTests
{
    [TestCase(PubsubRouter.FloodsubProtocolVersion)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13)]
    public async Task RegisteredListenerGatesPayloadsAndBoundsRemoteMemberships(string protocol)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        PubsubSettings settings = new()
        {
            AutoConnect = false,
            HeartbeatInterval = int.MaxValue,
            MaxRemoteSubscriptionsPerPeer = 2,
            MaxRemoteSubscriptionTopicBytesPerPeer = 4,
            MaxRemoteSubscriptions = 2,
            MaxRemoteSubscriptionTopicBytes = 4,
        };
        await using ServiceProvider receiverServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub())
            .AddSingleton(settings).BuildServiceProvider();
        WireSender wire = new(protocol);
        await using ServiceProvider senderServices = new ServiceCollection()
            .AddLibp2p(builder => builder.AddProtocol(wire)).BuildServiceProvider();
        WireSender honestWire = new(protocol);
        await using ServiceProvider honestServices = new ServiceCollection()
            .AddLibp2p(builder => builder.AddProtocol(honestWire)).BuildServiceProvider();
        await using ILocalPeer receiver = receiverServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer sender = senderServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer honestSender = honestServices.GetRequiredService<IPeerFactory>().Create();
        await receiver.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        PubsubRouter router = receiverServices.GetRequiredService<PubsubRouter>();
        await router.StartAsync(receiver, deadline.Token);
        router.GetTopic("wanted");
        ConcurrentQueue<string> delivered = new();
        ConcurrentDictionary<byte, TaskCompletionSource> markers = new();
        router.OnMessage += (topic, peer, data) =>
        {
            delivered.Enqueue(topic);
            Assert.That(peer, Is.EqualTo(data[0] == 4 ? honestSender.Identity.PeerId : sender.Identity.PeerId));
            if (topic == "wanted" && markers.TryGetValue(data[0], out TaskCompletionSource? marker)) marker.TrySetResult();
        };
        ISession session = await sender.DialAsync(receiver.ListenAddresses.First(), deadline.Token);
        Assert.That(session.RemotePeerId, Is.EqualTo(receiver.Identity.PeerId), "The sender uses the authenticated production session.");
        Task stream = session.DialAsync(wire, deadline.Token);
        ISession? honestSession = null;
        Task honestStream = Task.CompletedTask;
        try
        {
            IChannel channel = await wire.Ready.Task.WaitAsync(deadline.Token);
            ulong sequence = 0;
            Rpc Signed(string topic, byte data, Identity? author = null)
            {
                author ??= sender.Identity;
                byte[] seqno = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(seqno, sequence++);
                Message message = new()
                {
                    Topic = topic,
                    From = ByteString.CopyFrom(author.PeerId.Bytes),
                    Seqno = ByteString.CopyFrom(seqno),
                    Data = ByteString.CopyFrom([data]),
                };
                message.Signature = ByteString.CopyFrom(author.Sign([.. Encoding.UTF8.GetBytes("libp2p-pubsub:"), .. message.ToByteArray()]));
                return new Rpc { Publish = { message } };
            }
            async Task Marker(byte value)
            {
                TaskCompletionSource processed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                markers[value] = processed;
                await channel.WriteSizeAndProtobufAsync(Signed("wanted", value));
                await processed.Task.WaitAsync(deadline.Token);
            }

            // Frame-valid floods exercise the registered decoder, not a direct router invocation.
            Rpc subscriptions = new();
            foreach (int i in Enumerable.Range(0, 20_000))
                subscriptions.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = $"remote-{i}", Subscribe = true });
            subscriptions.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "a", Subscribe = true });
            subscriptions.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "b", Subscribe = true });
            Assert.That(subscriptions.CalculateSize(), Is.LessThan(settings.MaxRpcBytes));
            await channel.WriteSizeAndProtobufAsync(subscriptions);
            Rpc unwanted = new();
            for (int i = 0; i < 300; i++) unwanted.Publish.Add(Signed($"unwanted-{i % 4}", 0).Publish[0]);
            await channel.WriteSizeAndProtobufAsync(unwanted);
            // A valid RSA signature with a non-interoperable exponent must also be dropped
            // by the registered listener. The following marker proves this frame was processed.
            Identity rsaSigner = new(keyType: KeyType.Rsa);
            PublicKey expensiveKey = RsaKeyFixtures.EquivalentLargeExponent(rsaSigner);
            Message expensive = Signed("wanted", 9).Publish[0];
            expensive.From = ByteString.CopyFrom((byte[])Multiformats.Hash.Multihash.Sum(Multiformats.Hash.HashType.SHA2_256, expensiveKey.ToByteArray()));
            expensive.ClearSignature();
            expensive.Signature = ByteString.CopyFrom(rsaSigner.Sign([.. Encoding.UTF8.GetBytes("libp2p-pubsub:"), .. expensive.ToByteArray()]));
            expensive.Key = expensiveKey.ToByteString();
            await channel.WriteSizeAndProtobufAsync(new Rpc { Publish = { expensive } });
            await Marker(1);
            var state = (IRoutingStateContainer)router;
            var table = protocol == PubsubRouter.FloodsubProtocolVersion ? state.FloodsubPeers : state.GossipsubPeers;
            lock (router)
            {
                Assert.That(table.Keys, Is.EquivalentTo(new[] { "a", "b" }));
            }
            Assert.That(delivered, Is.EqualTo(new[] { "wanted" }), "The marker proves all preceding unwanted payloads were processed.");

            await channel.WriteSizeAndProtobufAsync(new Rpc
            {
                Subscriptions =
                {
                    new Rpc.Types.SubOpts { Topicid = "a", Subscribe = false },
                    new Rpc.Types.SubOpts { Topicid = "unknown", Subscribe = false },
                }
            });
            await Marker(2);
            lock (router)
            {
                Assert.That(table.Keys, Is.EquivalentTo(new[] { "b" }));
            }
            await channel.WriteSizeAndProtobufAsync(new Rpc { Subscriptions = { new Rpc.Types.SubOpts { Topicid = "a", Subscribe = true } } });
            router.Subscribe("b");
            await channel.WriteSizeAndProtobufAsync(Signed("b", 3));
            await Marker(3);
            Assert.That(delivered, Is.EqualTo(new[] { "wanted", "wanted", "b", "wanted" }), "Late subscription retains discovery and admits payloads.");

            // Fill both global budgets, then admit a different authenticated peer on a wanted
            // topic. Its marker runs on the same stream after its subscription or GRAFT.
            lock (router) Assert.That(table.Keys, Is.EquivalentTo(new[] { "a", "b" }));
            router.Subscribe("c");
            honestSession = await honestSender.DialAsync(receiver.ListenAddresses.First(), deadline.Token);
            Assert.That(honestSession.RemotePeerId, Is.EqualTo(receiver.Identity.PeerId));
            honestStream = honestSession.DialAsync(honestWire, deadline.Token);
            IChannel honestChannel = await honestWire.Ready.Task.WaitAsync(deadline.Token);
            Rpc admission = protocol == PubsubRouter.FloodsubProtocolVersion
                ? new Rpc { Subscriptions = { new Rpc.Types.SubOpts { Topicid = "c", Subscribe = true } } }
                : new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "c" } } } };
            await honestChannel.WriteSizeAndProtobufAsync(admission);
            TaskCompletionSource honestProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            markers[4] = honestProcessed;
            await honestChannel.WriteSizeAndProtobufAsync(Signed("wanted", 4, honestSender.Identity));
            await honestProcessed.Task.WaitAsync(deadline.Token);
            lock (router)
            {
                Assert.That(table.Keys, Is.EquivalentTo(new[] { "b", "c" }), "Wanted membership evicts junk while protecting the subscribed membership.");
                Assert.That(table["c"], Has.Member(honestSender.Identity.PeerId));
                if (protocol != PubsubRouter.FloodsubProtocolVersion)
                    Assert.That(state.Mesh["c"], Has.Member(honestSender.Identity.PeerId));
            }
            Assert.That(delivered, Is.EqualTo(new[] { "wanted", "wanted", "b", "wanted", "wanted" }));
            await honestSession.DisconnectAsync();
            await session.DisconnectAsync();
            while (state.ConnectedPeers.Count != 0) await Task.Delay(10, deadline.Token);
            lock (router)
            {
                Assert.That(table, Is.Empty, "Disconnect releases all topic keys.");
            }
        }
        finally
        {
            if (honestSession is not null) await honestSession.DisconnectAsync();
            await session.DisconnectAsync();
            await router.DisposeAsync();
            foreach (Task ownedStream in new[] { stream, honestStream })
            {
                try
                {
                    await ownedStream.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (ownedStream.IsCanceled)
                {
                    // Closing the authenticated session cancels its application stream.
                }
            }
        }
    }

    private sealed class WireSender(string id) : ISessionProtocol
    {
        public string Id => id;
        public TaskCompletionSource<IChannel> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            Ready.TrySetResult(channel);
            await channel;
        }
        public async Task ListenAsync(IChannel channel, ISessionContext context) => await channel;
    }
}
