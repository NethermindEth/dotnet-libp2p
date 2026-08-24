// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class DirectPeersTests
{
    [Test]
    public void DirectPeers_ForwardValidMessagesDespitePeerScores()
    {
        const string topic = "topic";
        Multiaddress senderAddress = TestPeers.Multiaddr(1);
        Multiaddress receiverAddress = TestPeers.Multiaddr(2);
        PeerId senderPeerId = senderAddress.GetPeerId()!;
        PeerId receiverPeerId = receiverAddress.GetPeerId()!;
        PubsubRouter router = new(
            new PeerStore(),
            new PubsubSettings { DirectPeers = [senderAddress, receiverAddress] });
        ITopic localTopic = router.GetTopic(topic);
        List<Rpc> senderRpcs = [];
        List<Rpc> receiverRpcs = [];
        TaskCompletionSource senderConnection = new();
        TaskCompletionSource receiverConnection = new();
        router.OutboundConnection(senderAddress, PubsubRouter.GossipsubProtocolVersionV11, senderConnection.Task, senderRpcs.Add);
        router.OutboundConnection(receiverAddress, PubsubRouter.GossipsubProtocolVersionV11, receiverConnection.Task, receiverRpcs.Add);
        router.OnRpc(senderPeerId, new Rpc().WithTopics([topic], []));
        router.OnRpc(receiverPeerId, new Rpc().WithTopics([topic], []));

        router.SetAppSpecificScore(senderPeerId, -20);
        router.SetAppSpecificScore(receiverPeerId, -20);
        senderRpcs.Clear();
        receiverRpcs.Clear();

        PeerId? receivedFrom = null;
        localTopic.OnMessage += (peerId, _) => receivedFrom = peerId;
        Identity author = TestPeers.Identity(1);
        router.OnRpc(senderPeerId, new Rpc().WithMessages(topic, 1, author.PeerId.Bytes, [1, 2, 3], author));

        Assert.Multiple(() =>
        {
            Assert.That(receivedFrom, Is.EqualTo(senderPeerId));
            Assert.That(receiverRpcs.Single().Publish.Single().Data.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(senderRpcs, Is.Empty);
        });

        senderConnection.SetResult();
        receiverConnection.SetResult();
    }

    [Test]
    public void DirectPeers_AreNeverAddedToTheMesh()
    {
        const string topic = "topic";
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        Multiaddress firstMeshAddress = TestPeers.Multiaddr(2);
        Multiaddress secondMeshAddress = TestPeers.Multiaddr(3);
        PubsubRouter router = new(
            new PeerStore(),
            new PubsubSettings { DirectPeers = [directAddress] });
        IRoutingStateContainer state = router;
        _ = router.GetTopic(topic);
        TaskCompletionSource connection = new();

        foreach (Multiaddress address in new[] { directAddress, firstMeshAddress, secondMeshAddress })
        {
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV11, connection.Task, _ => { });
            router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics([topic], []));
        }

        router.Heartbeat().GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[topic], Has.Member(directAddress.GetPeerId()));
            Assert.That(state.Mesh[topic], Does.Not.Contain(directAddress.GetPeerId()));
            Assert.That(state.Mesh[topic], Has.Count.EqualTo(2));
        });

        connection.SetResult();
    }

    [Test]
    public void DirectPeerGrafts_AreRejectedWithPrune()
    {
        const string topic = "topic";
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        PeerId directPeerId = directAddress.GetPeerId()!;
        PubsubRouter router = new(
            new PeerStore(),
            new PubsubSettings { DirectPeers = [directAddress], PruneBackoff = 2_000 });
        _ = router.GetTopic(topic);
        List<Rpc> sentRpcs = [];
        TaskCompletionSource connection = new();
        router.OutboundConnection(directAddress, PubsubRouter.GossipsubProtocolVersionV11, connection.Task, sentRpcs.Add);
        sentRpcs.Clear();

        Rpc graft = new() { Control = new ControlMessage() };
        graft.Control.Graft.Add(new ControlGraft { TopicID = topic });
        router.OnRpc(directPeerId, graft);

        ControlPrune prune = sentRpcs.Single().Control.Prune.Single();
        Assert.Multiple(() =>
        {
            Assert.That(prune.TopicID, Is.EqualTo(topic));
            Assert.That(prune.Backoff, Is.EqualTo(2));
        });
        connection.SetResult();
    }

    [Test]
    public async Task Router_ConnectsConfiguredDirectPeersAtStartup()
    {
        PeerStore peerStore = new();
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        PeerId directPeerId = directAddress.GetPeerId()!;
        peerStore.GetPeerInfo(directPeerId).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        PubsubRouter router = new(peerStore, new PubsubSettings { DirectPeers = [directAddress] });

        TaskCompletionSource protocolDialed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(directAddress);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            protocolDialed.TrySetResult();
            return Task.CompletedTask;
        });

        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(session);

        using CancellationTokenSource cancellation = new();
        await router.StartAsync(localPeer, cancellation.Token);

        await protocolDialed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = localPeer.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        _ = session.Received(1).DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>());

        cancellation.Cancel();
    }

    [Test]
    public void DirectPeers_RequirePeerIdsInTheirAddresses()
    {
        PubsubSettings settings = new()
        {
            DirectPeers = new[] { Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001") },
        };

        Assert.That(() => new PubsubRouter(new PeerStore(), settings), Throws.TypeOf<ArgumentException>());
    }
}
