// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class IwantTransmissionTests
{
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task PendingRequestDoesNotExpireAndAResponseBeforeWriteCompletionCannotBeRearmed(bool fulfill, bool queued)
    {
        Clock clock = new();
        PubsubSettings settings = new() { AutoConnect = false, HeartbeatInterval = int.MaxValue, DecayInterval = int.MaxValue, IWantFollowupTime = 100 };
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        OutboundWorkTests.PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = OutboundWorkTests.Open(router, writer, protocol: PubsubRouter.GossipsubProtocolVersionV11);
        if (queued)
        {
            peer.Send(new());
            await writer.Started.Task.WaitAsync(Deadline);
        }
        peer.Send(OutboundWorkTests.Iwant());
        await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        clock.Now += TimeSpan.FromHours(1);
        await router.Heartbeat();
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        if (fulfill)
        {
            var tracker = (IwantPromiseTracker)typeof(PubsubRouter).GetField("_iwantPromises", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(router)!;
            tracker.Fulfill(new MessageId([1]));
        }
        writer.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        await router.Heartbeat();
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero, "A successful write starts a fresh follow-up interval.");
        clock.Now += TimeSpan.FromMilliseconds(settings.IWantFollowupTime + 1);
        await router.Heartbeat();
        Assert.That(peer.Score.BehaviorPenalty, Is.EqualTo(fulfill ? 0 : 1));
        Assert.That(router.IwantPromiseCount, Is.Zero);
        await router.Heartbeat();
        Assert.That(peer.Score.BehaviorPenalty, Is.EqualTo(fulfill ? 0 : 1));
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public async Task IhaveDispatchOnlyScoresSuccessfullyWrittenUnfulfilledRequests(bool drop, bool fulfill)
    {
        Clock clock = new();
        PubsubSettings settings = new()
        {
            AutoConnect = false,
            HeartbeatInterval = int.MaxValue,
            DecayInterval = int.MaxValue,
            MaxOutboundRpcEntriesPerPeer = drop ? 1 : 2,
            IWantFollowupTime = 100,
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()),
        };
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        router.GetTopic("topic");
        OutboundWorkTests.PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, (frame, token) => RouterTestExtensions.Decode(frame).Subscriptions.Count != 0
                ? ValueTask.FromResult(IOResult.Ok) : writer.Write(frame, token), writer.Abort, writer.Disconnect)!;
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        peer.Send(new Rpc());
        await writer.Started.Task.WaitAsync(Deadline);
        await router.OnRpcAsync(peer.PeerId, new Rpc
        {
            Control = new ControlMessage { Ihave = { new ControlIHave { TopicID = "topic", MessageIDs = { ByteString.CopyFrom([1]) } } } },
        });
        Assert.That(peer.Control.IHaveRequested, Is.EqualTo(1));
        Assert.That(router.IwantPromiseCount, Is.EqualTo(drop ? 0 : 1));
        clock.Now += TimeSpan.FromHours(1);
        await router.Heartbeat();
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        if (fulfill)
        {
            await router.OnRpcAsync(peer.PeerId, new Rpc { Publish = { new Message { Topic = "topic", Data = ByteString.CopyFrom([1]) } } });
            Assert.That(router.IwantPromiseCount, Is.Zero);
        }
        writer.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        clock.Now += TimeSpan.FromMilliseconds(settings.IWantFollowupTime + 1);
        await router.Heartbeat();
        Assert.That(peer.Score.BehaviorPenalty, Is.EqualTo(drop || fulfill ? 0 : 1));
    }

    [Test]
    public async Task RejectedRequestsHaveNoPromiseAndQueuedRequestsCannotExpire()
    {
        Clock clock = new();
        PubsubSettings settings = new() { AutoConnect = false, MaxOutboundRpcEntriesPerPeer = 2, MaxIwantPromises = 1 };
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        OutboundWorkTests.PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = OutboundWorkTests.Open(router, writer, protocol: PubsubRouter.GossipsubProtocolVersionV11);
        peer.Send(new());
        await writer.Started.Task.WaitAsync(Deadline);
        peer.Send(new());
        Assert.That(peer.Send(OutboundWorkTests.Iwant()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        Assert.That(router.IwantPromiseCount, Is.Zero);
        writer.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage.Entries, Is.Zero);
        // Pending and armed promises share the tracker limit, including under refresh.
        IwantPromiseTracker tracker = new(1);
        object old = new(), replacement = new();
        var pending = tracker.Reserve(peer.PeerId, old, [new MessageId([1])]);
        Assert.That(tracker.Reserve(TestPeers.PeerId(2), replacement, [new MessageId([2])]), Is.Null);
        clock.Now += TimeSpan.FromHours(1);
        Assert.That(tracker.TakeExpired(clock.Now.UtcDateTime), Is.Empty);
        tracker.ClearOwner(old);
        var current = tracker.Reserve(peer.PeerId, replacement, [new MessageId([1])]);
        tracker.Arm(pending, clock.Now.UtcDateTime.AddSeconds(-1));
        Assert.That(tracker.TakeExpired(clock.Now.UtcDateTime), Is.Empty, "An old writer cannot arm a replacement generation's token.");
        tracker.Arm(current, clock.Now.UtcDateTime.AddSeconds(-1));
        Assert.That(tracker.TakeExpired(clock.Now.UtcDateTime), Is.EquivalentTo(new Dictionary<PeerId, int> { [peer.PeerId] = 1 }));
    }
}
