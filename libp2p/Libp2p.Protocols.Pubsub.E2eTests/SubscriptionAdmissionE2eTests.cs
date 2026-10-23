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
        };
        await using ServiceProvider receiverServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub())
            .AddSingleton(settings).BuildServiceProvider();
        WireSender wire = new(protocol);
        await using ServiceProvider senderServices = new ServiceCollection()
            .AddLibp2p(builder => builder.AddProtocol(wire)).BuildServiceProvider();
        await using ILocalPeer receiver = receiverServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer sender = senderServices.GetRequiredService<IPeerFactory>().Create();
        await receiver.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        PubsubRouter router = receiverServices.GetRequiredService<PubsubRouter>();
        await router.StartAsync(receiver, deadline.Token);
        router.GetTopic("wanted");
        ConcurrentQueue<string> delivered = new();
        ConcurrentDictionary<byte, TaskCompletionSource> markers = new();
        router.OnMessage += (topic, peer, data) =>
        {
            delivered.Enqueue(topic);
            Assert.That(peer, Is.EqualTo(sender.Identity.PeerId));
            if (topic == "wanted" && markers.TryGetValue(data[0], out TaskCompletionSource? marker)) marker.TrySetResult();
        };
        ISession session = await sender.DialAsync(receiver.ListenAddresses.First(), deadline.Token);
        Assert.That(session.RemotePeerId, Is.EqualTo(receiver.Identity.PeerId), "The sender uses the authenticated production session.");
        Task stream = session.DialAsync(wire, deadline.Token);
        try
        {
            IChannel channel = await wire.Ready.Task.WaitAsync(deadline.Token);
            ulong sequence = 0;
            Rpc Signed(string topic, byte data)
            {
                byte[] seqno = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(seqno, sequence++);
                Message message = new()
                {
                    Topic = topic,
                    From = ByteString.CopyFrom(sender.Identity.PeerId.Bytes),
                    Seqno = ByteString.CopyFrom(seqno),
                    Data = ByteString.CopyFrom([data]),
                };
                message.Signature = ByteString.CopyFrom(sender.Identity.Sign([.. Encoding.UTF8.GetBytes("libp2p-pubsub:"), .. message.ToByteArray()]));
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
            router.Subscribe("b");
            await channel.WriteSizeAndProtobufAsync(Signed("b", 3));
            await Marker(3);
            Assert.That(delivered, Is.EqualTo(new[] { "wanted", "wanted", "b", "wanted" }), "Late subscription retains discovery and admits payloads.");
            await session.DisconnectAsync();
            while (state.ConnectedPeers.Count != 0) await Task.Delay(10, deadline.Token);
            lock (router)
            {
                Assert.That(table, Is.Empty, "Disconnect releases all topic keys.");
            }
        }
        finally
        {
            await session.DisconnectAsync();
            await router.DisposeAsync();
            try
            {
                await stream.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (stream.IsCanceled)
            {
                // Closing the authenticated session cancels its application stream.
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
