// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class GossipsubV13ProtocolTests
{
    [TestCase(false, TestName = "Router_PenalizesExtensionsInSecondRpc")]
    [TestCase(true, TestName = "Router_PenalizesRepeatedExtensions")]
    public void Router_PenalizesExtensionsAfterFirstRpc(bool firstRpcHasExtensions)
    {
        using PubsubRouter router = CreateConnectedRouter(PubsubRouter.GossipsubProtocolVersionV13);
        PeerId peerId = TestPeers.PeerId(1);
        double initialScore = GetPeerScore(router, peerId);

        router.OnRpc(peerId, firstRpcHasExtensions ? CreateExtensionsRpc() : new Rpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: true);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));

        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: false);
        double penalizedScore = GetPeerScore(router, peerId);
        Assert.That(penalizedScore, Is.LessThan(initialScore));

        router.OnRpc(peerId, new Rpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: false);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(penalizedScore),
            "Subsequent RPCs without extensions must not incur a penalty.");

        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: false);
        Assert.That(GetPeerScore(router, peerId), Is.LessThan(penalizedScore),
            "Each additional extensions advertisement must incur a penalty.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Router_AllowsFirstRpcWithOrWithoutExtensions(bool firstRpcHasExtensions)
    {
        using PubsubRouter router = CreateConnectedRouter(PubsubRouter.GossipsubProtocolVersionV13);
        PeerId peerId = TestPeers.PeerId(1);
        double initialScore = GetPeerScore(router, peerId);

        router.OnRpc(peerId, firstRpcHasExtensions ? CreateExtensionsRpc() : new Rpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: true);
        router.OnRpc(peerId, new Rpc { Control = new ControlMessage() }, PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: false);

        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));
    }

    [TestCase(PubsubRouter.FloodsubProtocolVersion)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12)]
    public void Router_IgnoresExtensionsOnOlderProtocols(string protocolId)
    {
        using PubsubRouter router = CreateConnectedRouter(protocolId);
        PeerId peerId = TestPeers.PeerId(1);
        double initialScore = GetPeerScore(router, peerId);

        router.OnRpc(peerId, CreateExtensionsRpc(), protocolId, isFirstRpc: true);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));

        router.OnRpc(peerId, CreateExtensionsRpc(), protocolId, isFirstRpc: false);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));
    }

    [Test]
    public void Router_AllowsExtensionsInTheFirstRpcOnEachStream()
    {
        using PubsubRouter router = CreateConnectedRouter(PubsubRouter.GossipsubProtocolVersionV13);
        PeerId peerId = TestPeers.PeerId(1);
        double initialScore = GetPeerScore(router, peerId);

        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: true);
        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: true);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));

        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: false);
        Assert.That(GetPeerScore(router, peerId), Is.LessThan(initialScore));
    }

    [Test]
    public void Router_UsesTheIncomingStreamsProtocolForExtensions()
    {
        using PubsubRouter router = CreateConnectedRouter(PubsubRouter.GossipsubProtocolVersionV13);
        PeerId peerId = TestPeers.PeerId(1);
        double initialScore = GetPeerScore(router, peerId);

        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV12, isFirstRpc: false);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));

        router.OnRpc(peerId, CreateExtensionsRpc(), PubsubRouter.GossipsubProtocolVersionV13, isFirstRpc: true);
        Assert.That(GetPeerScore(router, peerId), Is.EqualTo(initialScore));
    }

    private static PubsubRouter CreateConnectedRouter(string protocolId)
    {
        PubsubRouter router = new(new PeerStore());
        TaskCompletionSource connectionLifetime = new();
        router.OutboundConnection(TestPeers.Multiaddr(1), protocolId, connectionLifetime.Task, _ => { });
        Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Does.Contain(TestPeers.PeerId(1)));
        return router;
    }

    private static Rpc CreateExtensionsRpc() => new()
    {
        Control = new ControlMessage
        {
            Extensions = new ControlExtensions { PartialMessages = true },
        },
    };

    private static double GetPeerScore(PubsubRouter router, PeerId peerId) =>
        (double)typeof(PubsubRouter)
            .GetMethod("GetPeerScore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(router, [peerId])!;

    [Test]
    public void Protocol_UsesTheV13ProtocolId()
    {
        PubsubRouter router = new(new PeerStore());

        GossipsubProtocolV13 protocol = new(router);

        Assert.That(protocol.Id, Is.EqualTo(PubsubRouter.GossipsubProtocolVersionV13));
    }

    [Test]
    public void ExtensionRegistryMessages_RoundTrip()
    {
        Rpc rpc = new()
        {
            Control = new ControlMessage
            {
                Extensions = new ControlExtensions { PartialMessages = true },
            },
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8("topic"),
                GroupID = ByteString.CopyFrom([1, 2]),
                PartialMessage = ByteString.CopyFrom([3]),
                PartsMetadata = ByteString.CopyFrom([4]),
            },
        };
        rpc.Subscriptions.Add(new Rpc.Types.SubOpts
        {
            Subscribe = true,
            Topicid = "topic",
            RequestsPartial = true,
            SupportsSendingPartial = true,
        });

        Rpc decoded = Rpc.Parser.ParseFrom(rpc.ToByteArray());

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Control.Extensions.PartialMessages, Is.True);
            Assert.That(decoded.Partial.TopicID.ToStringUtf8(), Is.EqualTo("topic"));
            Assert.That(decoded.Partial.GroupID.ToByteArray(), Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(decoded.Subscriptions.Single().RequestsPartial, Is.True);
            Assert.That(decoded.Subscriptions.Single().SupportsSendingPartial, Is.True);
        });
    }

    [Test]
    public void ExtensionRegistry_PreservesOpaquePartialMessageTopicBytes()
    {
        Rpc rpc = new()
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFrom([0xff, 0x00, 0x80]),
                GroupID = ByteString.CopyFrom([1]),
                PartialMessage = ByteString.CopyFrom([2]),
            },
        };

        Rpc decoded = Rpc.Parser.ParseFrom(rpc.ToByteArray());

        Assert.That(decoded.Partial.TopicID.ToByteArray(), Is.EqualTo(new byte[] { 0xff, 0x00, 0x80 }));
    }

    [Test]
    public async Task Router_PrefersV13WhenTheRemotePeerAdvertisesIt()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        Multiaddress remoteAddress = TestPeers.Multiaddr(1);
        PeerId remotePeerId = remoteAddress.GetPeerId()!;
        peerStore.GetPeerInfo(remotePeerId).SupportedProtocols = [
            PubsubRouter.GossipsubProtocolVersionV12,
            PubsubRouter.GossipsubProtocolVersionV13,
        ];

        TaskCompletionSource selectedProtocol = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(remoteAddress);
        session.DialAsync<GossipsubProtocolV13>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            selectedProtocol.TrySetResult();
            return Task.CompletedTask;
        });

        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(session);

        using CancellationTokenSource cancellation = new();
        await router.StartAsync(localPeer, cancellation.Token);
        peerStore.Discover([remoteAddress]);

        await selectedProtocol.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = session.Received(1).DialAsync<GossipsubProtocolV13>(Arg.Any<CancellationToken>());

        cancellation.Cancel();
    }
}
