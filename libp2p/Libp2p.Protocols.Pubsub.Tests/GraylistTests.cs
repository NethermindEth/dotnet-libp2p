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
        router.FlushOutboundAsync().GetAwaiter().GetResult();
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

    [TestCase(false)]
    [TestCase(true)]
    public void Messages_AreIgnoredFromGraylistedPeers(bool graylisted)
    {
        using PubsubRouter router = CreateRouter(new PubsubSettings
        {
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()),
        });
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);
        router.VerifyMessage = (_, _) => MessageValidity.Accepted;
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), _ => { });
        if (graylisted)
        {
            router.SetAppSpecificScore(peerId, GraylistedAppScore);
        }

        Rpc publish = new();
        publish.Publish.Add(new Message { Topic = Topic, Data = ByteString.CopyFrom([1]) });
        router.OnRpc(peerId, publish);

        Assert.That(deliveries, Has.Count.EqualTo(graylisted ? 0 : 1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Idontwant_IsIgnoredFromGraylistedPeers(bool graylisted)
    {
        // Keep the gossip and publish thresholds out of the way so that a graylisted peer still
        // receives the local publish, and only the graylist gate decides whether its IDONTWANT counts.
        using PubsubRouter router = CreateRouter(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            GossipThreshold = -1_000,
            PublishThreshold = -1_000,
            GraylistThreshold = -100,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()),
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
        if (graylisted)
        {
            router.SetAppSpecificScore(peerId, GraylistedAppScore);
        }

        Rpc idontwant = new() { Control = new ControlMessage() };
        idontwant.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom([1, 2, 3]) } });
        router.OnRpc(peerId, idontwant);
        sent.Clear();

        router.Publish(Topic, [1, 2, 3]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        cancellation.Cancel();

        Assert.That(sent.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(graylisted ? 1 : 0));
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

    [TestCase(14, false, false, -100)]
    [TestCase(15, false, false, -100)]
    [TestCase(15, true, false, -100)]
    [TestCase(15, false, false, -112.5)]
    [TestCase(15, true, false, -112.5)]
    [TestCase(15, false, true, -100)]
    [TestCase(15, true, true, -100)]
    public void InvalidMessagePenalty_AppliesToTheRestOfTheRpc(int invalidCount, bool invalidSignature, bool direct, double threshold)
    {
        Multiaddress address = TestPeers.Multiaddr(1);
        using PubsubRouter router = CreateRouter(new PubsubSettings
        {
            DefaultSignaturePolicy = invalidSignature ? PubsubSettings.SignaturePolicy.StrictSign : PubsubSettings.SignaturePolicy.StrictNoSign,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()),
            DirectPeers = direct ? [address] : [],
            GraylistThreshold = threshold,
        });
        IRoutingStateContainer state = router;
        _ = router.GetTopic(Topic);
        router.VerifyMessage = (_, _) => MessageValidity.Rejected;
        PeerId peerId = Connect(router, address, _ => { });

        Rpc rpc = Graft().WithTopics([Topic], []);
        for (int i = 0; i < invalidCount; i++)
        {
            rpc.Publish.Add(new Message { Topic = Topic, Data = ByteString.CopyFrom(BitConverter.GetBytes(i)) });
        }
        router.OnRpc(peerId, rpc);

        double score = router.GetPeerScore(peerId);
        Assert.That(score, Is.EqualTo(invalidCount == 14 ? -98 : -112.5));
        bool allowed = direct || score >= threshold;
        bool subscribed = state.GossipsubPeers.TryGetValue(Topic, out HashSet<PeerId>? peers) && peers.Contains(peerId);
        Assert.Multiple(() =>
        {
            Assert.That(subscribed, Is.EqualTo(allowed));
            Assert.That(state.Mesh[Topic].Contains(peerId), Is.EqualTo(allowed && !direct));
        });
    }

    [TestCase(-99, false)]
    [TestCase(-100, false)]
    [TestCase(-101, false)]
    [TestCase(-101, true)]
    public void UnsolicitedPartialPenalty_AppliesToTheRestOfTheRpc(double penaltyWeight, bool direct)
    {
        Multiaddress address = TestPeers.Multiaddr(1);
        using PubsubRouter router = CreateRouter(new PubsubSettings
        {
            EnablePartialMessages = true,
            BehaviorPenaltyWeight = penaltyWeight,
            GossipThreshold = -1_000,
            DirectPeers = direct ? [address] : [],
        });
        _ = router.GetTopic(Topic);
        List<Rpc> sent = [];
        PeerId peerId = Connect(router, address, sent.Add, PubsubRouter.GossipsubProtocolVersionV13);
        router.OnRpc(peerId, new Rpc
        {
            Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } },
        });
        sent.Clear();

        Rpc rpc = new() { Control = new ControlMessage() };
        rpc.Partial = new PartialMessagesExtension
        {
            TopicID = ByteString.CopyFromUtf8(Topic),
            GroupID = ByteString.CopyFrom([1]),
            PartialMessage = ByteString.CopyFrom([2]),
        };
        rpc.Control.Ihave.Add(new ControlIHave { TopicID = Topic, MessageIDs = { ByteString.CopyFrom([3]) } });
        router.OnRpc(peerId, rpc, isFirstRpc: false);

        Assert.That(router.GetPeerScore(peerId), Is.EqualTo(penaltyWeight));
        Assert.That(sent.SelectMany(response => response.Control?.Iwant.SelectMany(want => want.MessageIDs) ?? []),
            Is.EqualTo(direct || penaltyWeight >= -100 ? new[] { ByteString.CopyFrom([3]) } : []));
    }

    [TestCase(-99)]
    [TestCase(-100)]
    [TestCase(-101)]
    public void BackoffGraftPenalty_AppliesToTheRestOfTheRpc(double penaltyWeight)
    {
        using PubsubRouter router = CreateRouter(new PubsubSettings
        {
            BehaviorPenaltyWeight = penaltyWeight,
            GossipThreshold = -1_000,
        });
        _ = router.GetTopic(Topic);
        List<Rpc> sent = [];
        PeerId peerId = Connect(router, TestPeers.Multiaddr(1), sent.Add);
        router.OnRpc(peerId, Graft());
        Rpc prune = new() { Control = new ControlMessage() };
        prune.Control.Prune.Add(new ControlPrune { TopicID = Topic, Backoff = 3_600 });
        router.OnRpc(peerId, prune);
        Assert.That(((IRoutingStateContainer)router).Mesh[Topic], Does.Not.Contain(peerId));
        sent.Clear();

        Rpc rpc = Graft();
        rpc.Control.Ihave.Add(new ControlIHave { TopicID = Topic, MessageIDs = { ByteString.CopyFrom([3]) } });
        router.OnRpc(peerId, rpc);

        Assert.That(router.GetPeerScore(peerId), Is.EqualTo(penaltyWeight));
        Assert.Multiple(() =>
        {
            Assert.That(sent.SelectMany(response => response.Control?.Prune.Select(p => p.TopicID) ?? []), Is.EqualTo(new[] { Topic }));
            Assert.That(sent.SelectMany(response => response.Control?.Iwant.SelectMany(want => want.MessageIDs) ?? []),
                Is.EqualTo(penaltyWeight >= -100 ? new[] { ByteString.CopyFrom([3]) } : []));
        });
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
