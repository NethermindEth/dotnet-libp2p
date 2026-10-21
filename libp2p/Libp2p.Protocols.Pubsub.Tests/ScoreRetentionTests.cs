// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

/// <summary>
/// Gossipsub v1.1 keeps a disconnected peer's score for <see cref="PubsubSettings.RetainScore"/>,
/// so that a penalized peer cannot clear its score by reconnecting.
/// </summary>
[TestFixture]
public class ScoreRetentionTests
{
    private const string Topic = "topic";

    // A topic without configured parameters weighs P4 at 0.5 × -1, so 15 rejected messages
    // score 0.5 × -15² = -112.5, below the default graylist threshold of -100.
    private const int InvalidMessages = 15;
    private const double PenalizedScore = -112.5;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase(false, TestName = "PenalizedPeer_KeepsItsScore_WhenItReconnects(outbound)")]
    [TestCase(true, TestName = "PenalizedPeer_KeepsItsScore_WhenItReconnects(inbound)")]
    public async Task PenalizedPeer_KeepsItsScore_WhenItReconnects(bool inbound)
    {
        using PubsubRouter router = await CreateStartedRouterAsync();
        Multiaddress address = TestPeers.Multiaddr(1);
        TaskCompletionSource first = Connect(router, address);
        PeerId peerId = address.GetPeerId()!;
        Penalize(router, peerId);
        Assert.That(router.GetPeerScore(peerId), Is.EqualTo(PenalizedScore), "precondition: the peer is penalized");

        Disconnect(router, peerId, first);
        if (inbound) ConnectInbound(router, address);
        else Connect(router, address);

        Assert.That(router.GetPeerScore(peerId), Is.EqualTo(PenalizedScore));
        router.OnRpc(peerId, new Rpc().WithTopics(["other"], []));
        Assert.That(IsSubscribed(router, "other", peerId), Is.False, "The reconnected peer is still graylisted.");
    }

    /// <remarks>
    /// A second peer stays connected and penalized, to show that decay did run meanwhile.
    /// </remarks>
    [Test]
    public async Task RetainedScore_DoesNotDecay_WhileThePeerIsAway()
    {
        using PubsubRouter router = await CreateStartedRouterAsync(new PubsubSettings { DecayInterval = 0 });
        Multiaddress away = TestPeers.Multiaddr(1);
        Multiaddress present = TestPeers.Multiaddr(2);
        TaskCompletionSource awayConnection = Connect(router, away);
        Connect(router, present);
        Penalize(router, away.GetPeerId()!);
        Penalize(router, present.GetPeerId()!, firstMessage: InvalidMessages);

        Disconnect(router, away.GetPeerId()!, awayConnection);
        for (int i = 0; i < 3; i++)
        {
            await ((IRoutingStateContainer)router).Heartbeat();
        }
        Assert.That(router.GetPeerScore(present.GetPeerId()!), Is.GreaterThan(PenalizedScore), "precondition: connected scores decayed");
        Connect(router, away);

        Assert.That(router.GetPeerScore(away.GetPeerId()!), Is.EqualTo(PenalizedScore));
    }

    [Test]
    public async Task RetainedScore_IsNotRestored_OnceRetentionHasExpired()
    {
        using PubsubRouter router = await CreateStartedRouterAsync(new PubsubSettings { RetainScore = 0 });
        Multiaddress address = TestPeers.Multiaddr(1);
        PeerId peerId = address.GetPeerId()!;
        TaskCompletionSource connection = Connect(router, address);
        Penalize(router, peerId);

        Disconnect(router, peerId, connection);
        Connect(router, address);

        Assert.That(router.GetPeerScore(peerId), Is.Zero);
    }

    [Test]
    public async Task RetainedScore_IsDiscarded_AtTheFirstDecayAfterRetentionExpires()
    {
        using PubsubRouter router = await CreateStartedRouterAsync(new PubsubSettings { RetainScore = 0, DecayInterval = 0 });
        Multiaddress address = TestPeers.Multiaddr(1);
        TaskCompletionSource connection = Connect(router, address);
        Penalize(router, address.GetPeerId()!);
        Disconnect(router, address.GetPeerId()!, connection);
        Assert.That(router.RetainedScoreCount, Is.EqualTo(1), "precondition: the score was retained");

        await ((IRoutingStateContainer)router).Heartbeat();

        Assert.That(router.RetainedScoreCount, Is.Zero);
    }

    /// <remarks>
    /// Only a negative score is kept: a peer that scored well, or simply has nothing against
    /// it, starts again from zero and leaves nothing behind.
    /// </remarks>
    [TestCase(5, TestName = "WellBehavedPeer_StartsFromZero_WhenItReconnects(positive)")]
    [TestCase(0, TestName = "WellBehavedPeer_StartsFromZero_WhenItReconnects(zero)")]
    public async Task WellBehavedPeer_StartsFromZero_WhenItReconnects(double appSpecificScore)
    {
        using PubsubRouter router = await CreateStartedRouterAsync();
        Multiaddress address = TestPeers.Multiaddr(1);
        PeerId peerId = address.GetPeerId()!;
        TaskCompletionSource connection = Connect(router, address);
        router.SetAppSpecificScore(peerId, appSpecificScore);
        Assert.That(router.GetPeerScore(peerId), Is.GreaterThanOrEqualTo(0), "precondition: nothing counts against the peer");

        Disconnect(router, peerId, connection);
        Assert.That(router.RetainedScoreCount, Is.Zero);
        Connect(router, address);

        Assert.That(router.GetPeerScore(peerId), Is.Zero);
    }

    /// <remarks>
    /// Retention freezes a penalty while the peer is away; it does not make it permanent.
    /// </remarks>
    [Test]
    public async Task RestoredScore_DecaysAgain_OnceThePeerIsBack()
    {
        using PubsubRouter router = await CreateStartedRouterAsync(new PubsubSettings { DecayInterval = 0 });
        Multiaddress address = TestPeers.Multiaddr(1);
        PeerId peerId = address.GetPeerId()!;
        TaskCompletionSource connection = Connect(router, address);
        Penalize(router, peerId);
        Disconnect(router, peerId, connection);
        Connect(router, address);
        Assert.That(router.GetPeerScore(peerId), Is.EqualTo(PenalizedScore), "precondition: the score was restored");

        await ((IRoutingStateContainer)router).Heartbeat();

        Assert.That(router.GetPeerScore(peerId), Is.GreaterThan(PenalizedScore));
    }

    [Test]
    public async Task RestoredScore_IsKeptAgain_WhenThePeerDisconnectsAgain()
    {
        using PubsubRouter router = await CreateStartedRouterAsync();
        Multiaddress address = TestPeers.Multiaddr(1);
        PeerId peerId = address.GetPeerId()!;
        TaskCompletionSource first = Connect(router, address);
        Penalize(router, peerId);
        Disconnect(router, peerId, first);
        TaskCompletionSource second = Connect(router, address);

        Disconnect(router, peerId, second);
        Assert.That(router.RetainedScoreCount, Is.EqualTo(1));
        Connect(router, address);

        Assert.That(router.GetPeerScore(peerId), Is.EqualTo(PenalizedScore));
    }

    /// <remarks>
    /// An app-specific score of 20, at the default weight of 10, outweighs the -112.5 penalty.
    /// </remarks>
    [Test]
    public async Task RestoredScore_IsForgotten_WhenThePeerRecoversBeforeItDisconnects()
    {
        using PubsubRouter router = await CreateStartedRouterAsync();
        Multiaddress address = TestPeers.Multiaddr(1);
        PeerId peerId = address.GetPeerId()!;
        TaskCompletionSource first = Connect(router, address);
        Penalize(router, peerId);
        Disconnect(router, peerId, first);
        TaskCompletionSource second = Connect(router, address);
        router.SetAppSpecificScore(peerId, 20);
        Assert.That(router.GetPeerScore(peerId), Is.Positive, "precondition: the peer recovered");

        Disconnect(router, peerId, second);
        Assert.That(router.RetainedScoreCount, Is.Zero);
        Connect(router, address);

        Assert.That(router.GetPeerScore(peerId), Is.Zero);
    }

    /// <remarks>
    /// Room for two scores, and three penalized peers leave: 15 rejected messages score -112.5,
    /// two score -2 and one scores -0.5. The least negative is dropped, whether it is already
    /// kept or the one arriving, and on a tie the kept one stays, so peers whose scores are no
    /// worse cannot push out a penalized one.
    /// </remarks>
    [TestCase(15, 1, 2, 1, TestName = "RetainedScores_AreBounded_DroppingTheLeastNegative(kept)")]
    [TestCase(15, 2, 1, 2, TestName = "RetainedScores_AreBounded_DroppingTheLeastNegative(arriving)")]
    [TestCase(15, 2, 2, 2, TestName = "RetainedScores_AreBounded_DroppingTheLeastNegative(tie)")]
    public async Task RetainedScores_AreBounded_DroppingTheLeastNegative(int first, int second, int third, int dropped)
    {
        using PubsubRouter router = await CreateStartedRouterAsync(new PubsubSettings { MaxRetainedScores = 2 });
        int[] invalidMessages = [first, second, third];
        Multiaddress[] addresses = [TestPeers.Multiaddr(1), TestPeers.Multiaddr(2), TestPeers.Multiaddr(3)];
        int nextMessage = 0;
        for (int i = 0; i < addresses.Length; i++)
        {
            TaskCompletionSource connection = Connect(router, addresses[i]);
            Penalize(router, addresses[i].GetPeerId()!, nextMessage, invalidMessages[i]);
            nextMessage += invalidMessages[i];
            Disconnect(router, addresses[i].GetPeerId()!, connection);
        }
        Assert.That(router.RetainedScoreCount, Is.EqualTo(2));

        double[] restored = addresses.Select(address =>
        {
            Connect(router, address);
            return router.GetPeerScore(address.GetPeerId()!);
        }).ToArray();

        double[] expected = invalidMessages.Select((n, i) => i == dropped ? 0 : -0.5 * n * n).ToArray();
        Assert.That(restored, Is.EqualTo(expected));
    }

    /// <remarks>
    /// Room for two scores. The first, -112.5, is kept with no retention time left, so it has
    /// expired, but still takes a slot until the next decay. A score of -0.5 is kept next. When
    /// -2 arrives, the expired score is dropped rather than the least negative one.
    /// </remarks>
    [Test]
    public async Task RetainedScores_DropAnExpiredScoreFirst_WhenFull()
    {
        PubsubSettings settings = new() { MaxRetainedScores = 2, RetainScore = 0 };
        using PubsubRouter router = await CreateStartedRouterAsync(settings);
        Multiaddress expired = TestPeers.Multiaddr(1);
        Multiaddress mild = TestPeers.Multiaddr(2);
        Multiaddress arriving = TestPeers.Multiaddr(3);
        TaskCompletionSource connection = Connect(router, expired);
        Penalize(router, expired.GetPeerId()!);
        Disconnect(router, expired.GetPeerId()!, connection);

        settings.RetainScore = (int)TimeSpan.FromMinutes(10).TotalMilliseconds;
        connection = Connect(router, mild);
        Penalize(router, mild.GetPeerId()!, InvalidMessages, count: 1);
        Disconnect(router, mild.GetPeerId()!, connection);
        Assert.That(router.RetainedScoreCount, Is.EqualTo(2), "precondition: the set is full");
        connection = Connect(router, arriving);
        Penalize(router, arriving.GetPeerId()!, InvalidMessages + 1, count: 2);
        Disconnect(router, arriving.GetPeerId()!, connection);

        Connect(router, mild);
        Connect(router, arriving);
        Assert.That(router.GetPeerScore(mild.GetPeerId()!), Is.EqualTo(-0.5));
        Assert.That(router.GetPeerScore(arriving.GetPeerId()!), Is.EqualTo(-2));
    }

    private static async Task<PubsubRouter> CreateStartedRouterAsync(PubsubSettings? settings = null)
    {
        settings ??= new PubsubSettings();
        settings.ReconnectionAttempts = 0;
        // The tests run Heartbeat themselves, so decay happens only where they expect it.
        settings.HeartbeatInterval = int.MaxValue;
        settings.DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign;
        settings.GetMessageId = message => new MessageId(message.Data.ToByteArray());
        PubsubRouter router = new(new PeerStore(), settings);
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(100));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        await router.StartAsync(localPeer);
        _ = router.GetTopic(Topic);
        router.VerifyMessage = (_, _) => MessageValidity.Rejected;
        return router;
    }

    /// <summary>Connects outbound; completing the returned source ends the connection.</summary>
    private static TaskCompletionSource Connect(PubsubRouter router, Multiaddress address)
    {
        TaskCompletionSource lifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV11, lifetime.Task, _ => { });
        Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Does.Contain(address.GetPeerId()));
        return lifetime;
    }

    private static void ConnectInbound(PubsubRouter router, Multiaddress address)
    {
        router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task, () => Task.CompletedTask);
        Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Does.Contain(address.GetPeerId()));
    }

    private static void Disconnect(PubsubRouter router, PeerId peerId, TaskCompletionSource connection)
    {
        connection.SetResult();
        Assert.That(SpinWait.SpinUntil(() => !((IRoutingStateContainer)router).ConnectedPeers.Contains(peerId), Timeout), Is.True,
            "precondition: the router removed the peer");
        // RemovePeer leaves ConnectedPeers before it keeps the score, both under the router lock.
        lock (router) { }
    }

    /// <summary>
    /// Sends <paramref name="count"/> rejected messages numbered from <paramref name="firstMessage"/>;
    /// numbers already seen would be dropped as duplicates.
    /// </summary>
    private static void Penalize(PubsubRouter router, PeerId peerId, int firstMessage = 0, int count = InvalidMessages)
    {
        Rpc rpc = new Rpc().WithTopics([Topic], []);
        for (int i = firstMessage; i < firstMessage + count; i++)
        {
            rpc.Publish.Add(new Message { Topic = Topic, Data = ByteString.CopyFrom(BitConverter.GetBytes(i)) });
        }
        router.OnRpc(peerId, rpc);
    }

    private static bool IsSubscribed(PubsubRouter router, string topic, PeerId peerId)
    {
        lock (router)
        {
            return ((IRoutingStateContainer)router).GossipsubPeers.TryGetValue(topic, out HashSet<PeerId>? peers) && peers.Contains(peerId);
        }
    }
}
