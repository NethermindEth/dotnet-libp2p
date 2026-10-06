// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

/// <summary>
/// Gossipsub v1.1 score thresholds: all RPCs from a peer scored below
/// <see cref="PubsubSettings.GraylistThreshold"/> are ignored, direct peers excepted.
/// </summary>
[TestFixture]
public class GraylistTests
{
    private const string Topic = "topic";

    // With the default AppSpecificWeight (10) this puts the peer below the default GraylistThreshold (-100).
    private const double GraylistedAppScore = -20;

    [TestCase(false)]
    [TestCase(true)]
    public void Subscriptions_AreIgnoredFromGraylistedPeers(bool graylisted)
    {
        using PubsubRouter router = CreateRouter();
        IRoutingStateContainer state = router;
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), _ => { });
        if (graylisted)
        {
            router.SetAppSpecificScore(peerId, GraylistedAppScore);
        }

        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));

        bool subscribed = state.GossipsubPeers.TryGetValue(Topic, out HashSet<PeerId>? peers) && peers.Contains(peerId);
        Assert.That(subscribed, Is.EqualTo(!graylisted));
    }

    [Test]
    public void Subscriptions_CannotBeRemovedByGraylistedPeers()
    {
        using PubsubRouter router = CreateRouter();
        IRoutingStateContainer state = router;
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), _ => { });
        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));
        router.SetAppSpecificScore(peerId, GraylistedAppScore);

        router.OnRpc(peerId, new Rpc().WithTopics([], [Topic]));

        Assert.That(state.GossipsubPeers[Topic], Has.Member(peerId));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Grafts_AreIgnoredFromGraylistedPeers(bool graylisted)
    {
        using PubsubRouter router = CreateRouter();
        IRoutingStateContainer state = router;
        _ = router.GetTopic(Topic);
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), _ => { });
        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));
        if (graylisted)
        {
            router.SetAppSpecificScore(peerId, GraylistedAppScore);
        }

        router.OnRpc(peerId, Graft());

        Assert.That(state.Mesh[Topic].Contains(peerId), Is.EqualTo(!graylisted));
    }

    [Test]
    public void Prunes_AreIgnoredFromGraylistedPeers()
    {
        using PubsubRouter router = CreateRouter();
        IRoutingStateContainer state = router;
        _ = router.GetTopic(Topic);
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), _ => { });
        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));
        router.OnRpc(peerId, Graft());
        Assert.That(state.Mesh[Topic], Has.Member(peerId));
        router.SetAppSpecificScore(peerId, GraylistedAppScore);

        Rpc prune = new() { Control = new ControlMessage() };
        prune.Control.Prune.Add(new ControlPrune { TopicID = Topic });
        router.OnRpc(peerId, prune);

        Assert.That(state.Mesh[Topic], Has.Member(peerId));
    }

    [Test]
    public void Gossip_IsIgnoredFromGraylistedPeers()
    {
        // Keep the gossip threshold out of the way so that only the graylist gate can drop the IHAVE.
        using PubsubRouter router = CreateRouter(new PubsubSettings { GossipThreshold = -1_000, GraylistThreshold = -100 });
        _ = router.GetTopic(Topic);
        List<Rpc> sent = [];
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), sent.Add);
        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));
        router.SetAppSpecificScore(peerId, GraylistedAppScore);
        sent.Clear();

        Rpc ihave = new() { Control = new ControlMessage() };
        ihave.Control.Ihave.Add(new ControlIHave { TopicID = Topic, MessageIDs = { ByteString.CopyFrom([1]) } });
        router.OnRpc(peerId, ihave);

        Assert.That(sent, Is.Empty, "No IWANT may be sent in response to a graylisted peer's IHAVE.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Iwant_IsNotAnsweredForGraylistedPeers(bool graylisted)
    {
        // Keep the gossip threshold out of the way so that only the graylist gate can drop the IWANT.
        using PubsubRouter router = CreateRouter(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            GossipThreshold = -1_000,
            GraylistThreshold = -100,
        });
        using CancellationTokenSource cancellation = new();
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        await router.StartAsync(localPeer, cancellation.Token);
        _ = router.GetTopic(Topic);
        List<Rpc> sent = [];
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), sent.Add, PubsubRouter.GossipsubProtocolVersionV12);
        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));
        router.Publish(Topic, [1, 2, 3]);
        MessageId messageId = PubsubSettings.ConcatFromAndSeqno(sent.Last().Publish.Single());
        if (graylisted)
        {
            router.SetAppSpecificScore(peerId, GraylistedAppScore);
        }
        sent.Clear();

        Rpc iwant = new() { Control = new ControlMessage() };
        iwant.Control.Iwant.Add(new ControlIWant { MessageIDs = { ByteString.CopyFrom(messageId.Bytes) } });
        router.OnRpc(peerId, iwant);
        cancellation.Cancel();

        Assert.That(sent.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(graylisted ? 0 : 1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExtensionPenalty_AppliesToTheRestOfTheRpc(bool isFirstRpc)
    {
        // A single repeated-extensions penalty takes the peer below the default GraylistThreshold (-100).
        using PubsubRouter router = CreateRouter(new PubsubSettings { BehaviorPenaltyWeight = -200 });
        IRoutingStateContainer state = router;
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), _ => { }, PubsubRouter.GossipsubProtocolVersionV13);

        Rpc rpc = new Rpc().WithTopics([Topic], []);
        rpc.Control = new ControlMessage { Extensions = new ControlExtensions() };
        router.OnRpc(peerId, rpc, isFirstRpc: isFirstRpc);

        bool subscribed = state.GossipsubPeers.TryGetValue(Topic, out HashSet<PeerId>? peers) && peers.Contains(peerId);
        Assert.That(subscribed, Is.EqualTo(isFirstRpc));
    }

    [Test]
    public void DirectPeers_AreExemptFromTheGraylist()
    {
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        using PubsubRouter router = CreateRouter(new PubsubSettings { DirectPeers = [directAddress] });
        IRoutingStateContainer state = router;
        PeerId peerId = Connect(router, directAddress, _ => { });
        router.SetAppSpecificScore(peerId, GraylistedAppScore);

        router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));

        Assert.That(state.GossipsubPeers[Topic], Has.Member(peerId));
    }

    private static PubsubRouter CreateRouter(PubsubSettings? settings = null)
        => new(new PeerStore(), settings ?? new PubsubSettings());

    private static PeerId Connect(PubsubRouter router, Multiaddress address, Action<Rpc> send, string protocol = PubsubRouter.GossipsubProtocolVersionV11)
    {
        router.OutboundConnection(address, protocol, new TaskCompletionSource().Task, send);
        return address.GetPeerId()!;
    }

    private static Rpc Graft()
    {
        Rpc graft = new() { Control = new ControlMessage() };
        graft.Control.Graft.Add(new ControlGraft { TopicID = Topic });
        return graft;
    }
}
