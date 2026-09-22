// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PubsubRouterLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public void Topic_Unsubscribe_unsubscribes_a_subscribed_topic()
    {
        using PubsubRouter router = new(new PeerStore());
        ITopic topic = router.GetTopic("topic");
        Assert.That(topic.IsSubscribed, Is.True);

        topic.Unsubscribe();

        Assert.That(topic.IsSubscribed, Is.False);
    }

    [Test]
    public async Task DisposeAsync_cancels_and_awaits_owned_dials_without_caller_cancellation()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        ILocalPeer peer = CreatePeer();
        TaskCompletionSource<CancellationToken> dialStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>())
            .Returns(ci => DialForever(ci.ArgAt<CancellationToken>(1), dialStarted));

        await router.StartAsync(peer);
        peerStore.Discover([TestPeers.Multiaddr(1)]);
        CancellationToken dialToken = await dialStarted.Task.WaitAsync(Timeout);

        await router.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.That(dialToken.IsCancellationRequested, Is.True);

        peer.ClearReceivedCalls();
        peerStore.Discover([TestPeers.Multiaddr(2)]);
        await peer.DidNotReceive().DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Disposal_is_idempotent_and_blocks_restart()
    {
        PubsubRouter router = new(new PeerStore());
        ILocalPeer peer = CreatePeer();
        await router.StartAsync(peer);

        router.Dispose();
        await router.DisposeAsync().AsTask().WaitAsync(Timeout);
        router.Dispose();

        Assert.ThrowsAsync<ObjectDisposedException>(() => router.StartAsync(peer));
    }

    [Test]
    public async Task DisposeAsync_completes_when_router_was_never_started()
    {
        PubsubRouter router = new(new PeerStore());

        await router.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Test]
    public void Topic_resubscribe_is_announced_to_connected_peers()
    {
        using PubsubRouter router = new(new PeerStore());
        ITopic topic = router.GetTopic("topic");
        List<Rpc> sent = ConnectFloodsubPeer(router, subscribedTo: "topic");

        topic.Unsubscribe();
        topic.Subscribe();

        Assert.That(Announcements(sent, "topic"), Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void Subscribe_is_announced_when_a_peer_announced_the_topic_first()
    {
        using PubsubRouter router = new(new PeerStore());
        List<Rpc> sent = ConnectFloodsubPeer(router, subscribedTo: "topic");

        router.GetTopic("topic");

        Assert.That(Announcements(sent, "topic"), Is.EqualTo(new[] { true }));
    }

    [Test]
    public async Task DisposeAsync_closes_and_awaits_the_reverse_dial_of_an_inbound_stream()
    {
        PubsubRouter router = new(new PeerStore());
        await router.StartAsync(CreatePeer());
        FloodsubProtocol protocol = new(router);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        TaskCompletionSource<Task> reverseDial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DialAsync(protocol).Returns(_ =>
        {
            Task dial = protocol.DialAsync(new Channel(), context);
            reverseDial.TrySetResult(dial);
            return dial;
        });

        Task listen = protocol.ListenAsync(new Channel(), context);
        Task dial = await reverseDial.Task.WaitAsync(Timeout);

        await router.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.That(dial.IsCompleted, Is.True);
        await listen.WaitAsync(Timeout);
    }

    private static List<Rpc> ConnectFloodsubPeer(PubsubRouter router, string subscribedTo)
    {
        List<Rpc> sent = [];
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task, sent.Add);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([subscribedTo], []));
        sent.Clear();
        return sent;
    }

    private static bool[] Announcements(List<Rpc> sent, string topic) =>
        [.. sent.SelectMany(rpc => rpc.Subscriptions).Where(s => s.Topicid == topic).Select(s => s.Subscribe)];

    private static ILocalPeer CreatePeer()
    {
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Identity.Returns(TestPeers.Identity(0));
        peer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        return peer;
    }

    private static async Task<ISession> DialForever(CancellationToken token, TaskCompletionSource<CancellationToken> started)
    {
        started.TrySetResult(token);
        await Task.Delay(System.Threading.Timeout.Infinite, token);
        throw new InvalidOperationException("unreachable");
    }
}
