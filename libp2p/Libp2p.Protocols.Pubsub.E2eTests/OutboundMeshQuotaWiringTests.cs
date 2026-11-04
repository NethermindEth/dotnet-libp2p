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
using System.Threading.Channels;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class OutboundMeshQuotaWiringTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task QuotaUsesConnectionDirectionWhenRemoteOpensThePubsubStream(bool outbound)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        PubsubSettings settings = new()
        {
            HeartbeatInterval = int.MaxValue,
            Degree = 0,
            LowestDegree = 0,
            HighestDegree = 0,
            DOut = 1,
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()),
        };
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub()).AddSingleton(settings).BuildServiceProvider();
        WirePeer wire = new();
        await using ServiceProvider remoteServices = new ServiceCollection()
            .AddLibp2p(builder => builder.AddProtocol(wire)).BuildServiceProvider();
        await using ILocalPeer local = services.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer remote = remoteServices.GetRequiredService<IPeerFactory>().Create();
        await local.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], token);
        await remote.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], token);
        PubsubRouter router = services.GetRequiredService<PubsubRouter>();
        router.GetTopic("topic");
        router.GetTopic("graft");
        router.GetTopic("marker");
        Channel<byte> markers = System.Threading.Channels.Channel.CreateUnbounded<byte>();
        router.OnMessage += (topic, _, data) =>
        {
            if (topic == "marker") markers.Writer.TryWrite(data[0]);
        };

        if (outbound) await local.DialAsync(remote.ListenAddresses.First(), token);
        ISession session = await remote.DialAsync(local.ListenAddresses.First(), token);
        Task stream = Task.CompletedTask;
        try
        {
            // For outbound connections, the remote opens pubsub first. For inbound connections,
            // discovery opens our writer on the already established inbound session first.
            if (outbound) stream = session.DialAsync(wire, token);
            await router.StartAsync(local, token);
            await wire.Listening.Task.WaitAsync(token);
            if (!outbound) stream = session.DialAsync(wire, token);
            IChannel input = await wire.Dialed.Task.WaitAsync(token);

            async Task Receive(Rpc rpc, byte marker)
            {
                await input.WriteSizeAndProtobufAsync(rpc);
                await input.WriteSizeAndProtobufAsync(new Rpc
                {
                    Publish = { new Message { Topic = "marker", Data = ByteString.CopyFrom([marker]) } },
                });
                Assert.That(await markers.Reader.ReadAsync(token), Is.EqualTo(marker));
            }

            await Receive(new Rpc { Subscriptions = { new Rpc.Types.SubOpts { Topicid = "topic", Subscribe = true } } }, 1);
            await router.Heartbeat();
            lock (router)
            {
                Assert.That(((IRoutingStateContainer)router).Mesh["topic"].Contains(remote.Identity.PeerId), Is.EqualTo(outbound),
                    "Heartbeat must count the underlying connection, regardless of which side opened pubsub.");
            }
            if (outbound)
            {
                Rpc graft = await wire.ReadUntil(rpc => rpc.Control?.Graft.Any(g => g.TopicID == "topic") == true, token);
                Assert.That(graft.Control.Graft.Any(g => g.TopicID == "topic"), Is.True);
            }

            await Receive(new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "graft" } } } }, 2);
            lock (router)
                Assert.That(((IRoutingStateContainer)router).Mesh["graft"].Contains(remote.Identity.PeerId), Is.EqualTo(outbound),
                    "At D_hi, only a peer on an outbound connection may join.");
            if (!outbound)
                await wire.ReadUntil(rpc => rpc.Control?.Prune.Any(p => p.TopicID == "graft") == true, token);
        }
        finally
        {
            await session.DisconnectAsync();
            await router.DisposeAsync();
            try { await stream.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stream.IsCanceled) { }
        }
    }

    private sealed class WirePeer : ISessionProtocol
    {
        public string Id => PubsubRouter.GossipsubProtocolVersionV11;
        public TaskCompletionSource<IChannel> Dialed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Listening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<Rpc> _received = System.Threading.Channels.Channel.CreateUnbounded<Rpc>();

        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            Dialed.TrySetResult(channel);
            await channel;
        }

        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            Listening.TrySetResult();
            try
            {
                while (true)
                    await _received.Writer.WriteAsync(await channel.ReadPrefixedProtobufAsync(Rpc.Parser, context.Token), context.Token);
            }
            catch (OperationCanceledException) when (context.Token.IsCancellationRequested) { }
            catch (Nethermind.Libp2p.Core.Exceptions.ChannelClosedException) { }
        }

        public async Task<Rpc> ReadUntil(Func<Rpc, bool> predicate, CancellationToken token)
        {
            while (true)
            {
                Rpc rpc = await _received.Reader.ReadAsync(token);
                if (predicate(rpc)) return rpc;
            }
        }
    }
}
