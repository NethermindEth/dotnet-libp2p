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
        router.FlushOutboundAsync().GetAwaiter().GetResult();

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
    public async Task DisposeAsync_stops_and_joins_owned_work_when_peer_store_unsubscribe_fails()
    {
        IPeerStore store = Substitute.For<IPeerStore>();
        InvalidOperationException failure = new("Peer store unsubscribe failed.");
        store.When(s => s.OnNewPeer -= Arg.Any<Action<Multiaddress[]>>()).Do(_ => throw failure);
        PubsubRouter router = new(store);
        using CancellationTokenSource caller = new();
        await router.StartAsync(CreatePeer(), caller.Token);

        TaskCompletionSource streamClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource abortStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishAbort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PubsubRouter.PubsubPeer generation = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            streamClosed.Task, (_, _) => ValueTask.FromResult(IOResult.Ok), async () =>
            {
                abortStarted.TrySetResult();
                await finishAbort.Task;
            }, () => Task.CompletedTask)!;

        Task disposal = router.DisposeAsync().AsTask();
        try
        {
            Assert.That(await Task.WhenAny(abortStarted.Task, disposal).WaitAsync(Timeout), Is.SameAs(abortStarted.Task));
            Assert.That(router.Stopped.IsCancellationRequested, Is.True);
            Assert.That(disposal.IsCompleted, Is.False);
        }
        finally
        {
            finishAbort.TrySetResult();
            streamClosed.TrySetResult();
            caller.Cancel();
            await disposal.WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await generation.Completion.WaitAsync(Timeout);
        }

        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () => await disposal), Is.SameAs(failure));
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
    public async Task DisposeAsync_skips_unsubscribe_when_router_was_never_started()
    {
        IPeerStore store = Substitute.For<IPeerStore>();
        PubsubRouter router = new(store);

        await router.DisposeAsync().AsTask().WaitAsync(Timeout);

        store.DidNotReceiveWithAnyArgs().OnNewPeer -= null;
    }

    [Test]
    public void Topic_resubscribe_is_announced_to_connected_peers()
    {
        using PubsubRouter router = new(new PeerStore());
        ITopic topic = router.GetTopic("topic");
        List<Rpc> sent = ConnectFloodsubPeer(router, subscribedTo: "topic");

        topic.Unsubscribe();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        topic.Subscribe();
        router.FlushOutboundAsync().GetAwaiter().GetResult();

        Assert.That(Announcements(sent, "topic"), Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void Subscribe_is_announced_when_a_peer_announced_the_topic_first()
    {
        using PubsubRouter router = new(new PeerStore());
        List<Rpc> sent = ConnectFloodsubPeer(router, subscribedTo: "topic");

        router.GetTopic("topic");
        router.FlushOutboundAsync().GetAwaiter().GetResult();

        Assert.That(Announcements(sent, "topic"), Is.EqualTo(new[] { true }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposeAsync_closes_and_awaits_the_reverse_dial_of_an_inbound_stream(bool autoConnect)
    {
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = autoConnect });
        await router.StartAsync(CreatePeer());
        FloodsubProtocol protocol = new(router);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        TaskCompletionSource<Task> reverseDial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DialAsync(protocol, Arg.Is<CancellationToken>(token => token.CanBeCanceled)).Returns(_ =>
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
