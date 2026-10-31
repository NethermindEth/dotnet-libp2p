// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class OutboundWorkTests
{
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;
    private static Rpc Payload(int bytes = 16) => new() { Publish = { new Message { Data = ByteString.CopyFrom(new byte[bytes]) } } };
    private static int FrameSize(Rpc rpc) => rpc.CalculateSize() + VarInt.GetSizeInBytes(rpc.CalculateSize());
    private static PubsubSettings Settings() => new() { AutoConnect = false, HeartbeatInterval = int.MaxValue, DecayInterval = int.MaxValue };

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task CountAndBytesIncludeTheActiveWriteAndDropNewest(bool bytes, bool global)
    {
        PubsubSettings settings = Settings();
        Rpc rpc = Payload();
        if (bytes)
        {
            if (global) settings.MaxOutboundRpcBytes = 4L * FrameSize(rpc);
            else settings.MaxOutboundRpcBytesPerPeer = 2L * FrameSize(rpc);
        }
        else if (global) settings.MaxOutboundRpcEntries = 4;
        else settings.MaxOutboundRpcEntriesPerPeer = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = Open(router, writer);
        Assert.That(peer.Send(rpc), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(peer.Send(rpc), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Parallel.For(0, 200, _ => Assert.That(peer.Send(rpc), Is.EqualTo(PubsubRouter.SendResult.Dropped)));
        Assert.That(router.OutboundUsage, Is.EqualTo((2, 2L * FrameSize(rpc))));
        Assert.That(writer.Writes, Is.EqualTo(1));
        writer.Result.TrySetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GlobalCapacityIncludesOtherPeersAndDrainingGenerations(bool bytes)
    {
        PubsubSettings settings = Settings();
        if (bytes)
        {
            settings.MaxOutboundRpcBytesPerPeer = FrameSize(Payload());
            settings.MaxOutboundRpcBytes = 2L * FrameSize(Payload());
        }
        else
        {
            settings.MaxOutboundRpcEntriesPerPeer = 1;
            settings.MaxOutboundRpcEntries = 2;
        }
        await using PubsubRouter router = new(new PeerStore(), settings);
        PausedWriter first = new() { ReleaseOnAbort = false };
        PubsubRouter.PubsubPeer old = Open(router, first);
        old.Send(Payload());
        await first.Started.Task.WaitAsync(Deadline);
        PubsubRouter.PubsubPeer other = Open(router, new PausedWriter(), 2);
        Assert.That(other.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        // Necessary control loss stops the generation, but cannot release its running write.
        Assert.That(old.Send(new Rpc().WithTopics(["x"], [])), Is.EqualTo(PubsubRouter.SendResult.Closing));
        await first.Aborted.Task.WaitAsync(Deadline);
        PausedWriter second = new();
        PubsubRouter.PubsubPeer replacement = Open(router, second);
        Assert.That(replacement, Is.Not.SameAs(old));
        Assert.That(replacement.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        Assert.That(router.OutboundUsage.Entries, Is.EqualTo(1));
        first.Result.SetResult(IOResult.Aborted);
        await old.Completion.WaitAsync(Deadline);
        Assert.That(replacement.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await second.Started.Task.WaitAsync(Deadline);
        second.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
        Assert.That(replacement.Terminal, Is.False);
    }

    [Test]
    public async Task PreStreamQueueIsBoundedAndOwnsAnImmutableSnapshot()
    {
        PubsubSettings settings = Settings();
        settings.MaxOutboundRpcEntriesPerPeer = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.InboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, () => Task.CompletedTask, () => Task.CompletedTask);
        var peers = (ConcurrentDictionary<PeerId, PubsubRouter.PubsubPeer>)typeof(PubsubRouter)
            .GetField("peerState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
        PubsubRouter.PubsubPeer peer = peers[TestPeers.PeerId(1)];
        Rpc rpc = Payload();
        Rpc expected = rpc.Clone();
        Assert.That(peer.Send(rpc), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        rpc.Publish[0].Data = ByteString.CopyFrom([99]);
        Assert.That(peer.Send(new Rpc()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        ConcurrentQueue<Rpc> sent = [];
        Assert.That(router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, (frame, _) =>
            {
                sent.Enqueue(RouterTestExtensions.Decode(frame));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => Task.CompletedTask), Is.SameAs(peer));
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(sent.First(), Is.EqualTo(expected));
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase("subscribe", false)]
    [TestCase("unsubscribe", false)]
    [TestCase("graft", false)]
    [TestCase("prune", false)]
    [TestCase("subscribe", true)]
    [TestCase("unsubscribe", true)]
    [TestCase("graft", true)]
    [TestCase("prune", true)]
    public async Task NecessaryControlLossClosesTheGenerationWithoutAnOverloadPenalty(string control, bool bytes)
    {
        PubsubSettings settings = Settings();
        if (bytes) settings.MaxOutboundRpcBytesPerPeer = FrameSize(Payload());
        else settings.MaxOutboundRpcEntriesPerPeer = 1;
        await using PubsubRouter router = new(new PeerStore(), settings);
        PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = Open(router, writer);
        await router.OnRpcAsync(peer.PeerId, new Rpc().WithTopics(["remote"], []));
        Assert.That(peer.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await writer.Started.Task.WaitAsync(Deadline);
        Rpc update = control switch
        {
            "subscribe" => new Rpc().WithTopics(["local"], []),
            "unsubscribe" => new Rpc().WithTopics([], ["local"]),
            "graft" => new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "local" } } } },
            _ => new Rpc { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = "local" } } } },
        };
        Assert.That(peer.Send(update), Is.EqualTo(PubsubRouter.SendResult.Closing));
        Assert.That(peer.Terminal, Is.True);
        Assert.That(peer.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Closing));
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(writer.Disconnects, Is.EqualTo(1));
        Assert.That(peer.RemoteSubscriptions, Is.Empty);
        Assert.That(((IRoutingStateContainer)router).FloodsubPeers, Is.Empty);
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase("subscribe", false)]
    [TestCase("unsubscribe", false)]
    [TestCase("graft", false)]
    [TestCase("prune", false)]
    [TestCase("subscribe", true)]
    [TestCase("unsubscribe", true)]
    [TestCase("graft", true)]
    [TestCase("prune", true)]
    public async Task GlobalOptionalExhaustionPreservesRequiredControlForHealthyPeers(string control, bool bytes)
    {
        PubsubSettings settings = Settings();
        Rpc payload = Payload();
        if (bytes) settings.MaxOutboundRpcBytes = 2L * FrameSize(payload);
        else settings.MaxOutboundRpcEntries = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        int writes = 0, disconnects = 0;
        PubsubRouter.PubsubPeer healthy = router.OutboundConnection(TestPeers.Multiaddr(2), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, (_, _) =>
            {
                Interlocked.Increment(ref writes);
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; })!;
        Assert.That(healthy.Send(payload), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(healthy.Entries, Is.Zero, "The affected peer has already drained its previous write.");
        await router.OnRpcAsync(healthy.PeerId, new Rpc().WithTopics(["remote"], []));
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers["remote"], Has.Member(healthy.PeerId));
        PausedWriter stalled = new();
        PubsubRouter.PubsubPeer blocker = Open(router, stalled);
        Assert.That(blocker.Send(payload), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await stalled.Started.Task.WaitAsync(Deadline);
        Assert.That(blocker.Entries, Is.LessThan(settings.MaxOutboundRpcEntriesPerPeer));
        Assert.That(blocker.Bytes, Is.LessThan(settings.MaxOutboundRpcBytesPerPeer));
        PubsubRouter.SendResult excessOptional = blocker.Send(payload);
        Assert.That(healthy.Send(payload), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        Assert.That(healthy.Terminal, Is.False, "Global exhaustion alone does not close a peer sending optional traffic.");
        Rpc update = control switch
        {
            "subscribe" => new Rpc().WithTopics(["local"], []),
            "unsubscribe" => new Rpc().WithTopics([], ["local"]),
            "graft" => new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "local" } } } },
            _ => new Rpc { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = "local" } } } },
        };
        Assert.That(healthy.Send(update), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await healthy.Idle.Task.WaitAsync(Deadline);
        Assert.That(excessOptional, Is.EqualTo(PubsubRouter.SendResult.Dropped), "Optional traffic must leave shared headroom for control.");
        Assert.That(writes, Is.EqualTo(2));
        Assert.That(disconnects, Is.Zero);
        Assert.That(healthy.Terminal, Is.False);
        Assert.That(healthy.Score.BehaviorPenalty, Is.Zero);
        Assert.That(blocker.Score.BehaviorPenalty, Is.Zero);
        Assert.That(healthy.RemoteSubscriptions, Has.Count.EqualTo(1));
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers["remote"], Has.Member(healthy.PeerId));
        Assert.That(blocker.Terminal, Is.False);
        Assert.That(router.OutboundUsage, Is.EqualTo((1, (long)FrameSize(payload))), "The stalled write retains ownership of its reservation.");
        stalled.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task GlobalOptionalExhaustionAllowsTheRequiredHelloWithTheCurrentSnapshot(bool bytes, bool extensions)
    {
        PubsubSettings settings = Settings();
        settings.EnablePartialMessages = extensions;
        Rpc payload = Payload();
        if (bytes) settings.MaxOutboundRpcBytes = 2L * FrameSize(payload);
        else settings.MaxOutboundRpcEntries = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        if (!extensions) router.GetTopic("wanted");
        PausedWriter stalled = new();
        PubsubRouter.PubsubPeer blocker = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, (frame, token) => RouterTestExtensions.Decode(frame).Subscriptions.Count != 0
                ? ValueTask.FromResult(IOResult.Ok) : stalled.Write(frame, token), stalled.Abort, stalled.Disconnect)!;
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(blocker.Send(payload), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await stalled.Started.Task.WaitAsync(Deadline);
        string protocol = extensions ? PubsubRouter.GossipsubProtocolVersionV13 : PubsubRouter.FloodsubProtocolVersion;
        List<Rpc> sent = [];
        PubsubRouter.PubsubPeer replacement = router.OutboundConnection(TestPeers.Multiaddr(2), protocol, new TaskCompletionSource().Task,
            (frame, _) => { sent.Add(RouterTestExtensions.Decode(frame)); return ValueTask.FromResult(IOResult.Ok); },
            () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
        await replacement.Idle.Task.WaitAsync(Deadline);
        Assert.That(replacement.Terminal, Is.False);
        Assert.That(sent, Has.Count.EqualTo(1));
        if (extensions) Assert.That(sent[0].Control.Extensions.PartialMessages && replacement.ExtensionsSent, Is.True);
        else Assert.That(sent[0].Subscriptions.Select(sub => (sub.Topicid, sub.Subscribe)), Is.EqualTo(new[] { ("wanted", true) }));
        Assert.That(router.OutboundUsage, Is.EqualTo((1, (long)FrameSize(payload))));
        Assert.That(blocker.Terminal, Is.False);
        stalled.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task DefaultBudgetsKeepControlHeadroomWhenSeventeenPeersNeverRead()
    {
        PubsubSettings settings = Settings();
        settings.EnablePartialMessages = true;
        await using PubsubRouter router = new(new PeerStore(), settings);
        Rpc payload = Payload(1_000_000);
        for (int index = 1; index <= 17; index++)
        {
            PubsubRouter.PubsubPeer stalled = Open(router, new PausedWriter(), index);
            for (int n = 0; n < 5; n++) stalled.Send(payload);
        }
        long optionalBytes = settings.MaxOutboundRpcBytes - settings.MaxOutboundRpcBytesPerPeer;
        long remaining = optionalBytes - router.OutboundUsage.Bytes;
        Assert.That(remaining, Is.InRange(1, FrameSize(payload) - 1));
        Rpc filler = Payload((int)remaining - (FrameSize(payload) - 1_000_000));
        Assert.That(FrameSize(filler), Is.EqualTo(remaining));
        Assert.That(Open(router, new PausedWriter(), 18).Send(filler), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(router.OutboundUsage.Bytes, Is.EqualTo(60L * 1024 * 1024));
        ConcurrentQueue<Rpc> sent = [];
        int disconnects = 0;
        PubsubRouter.PubsubPeer healthy = router.OutboundConnection(TestPeers.Multiaddr(100), PubsubRouter.GossipsubProtocolVersionV13,
            new TaskCompletionSource().Task, (frame, _) =>
            {
                sent.Enqueue(RouterTestExtensions.Decode(frame));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; })!;
        await healthy.Idle.Task.WaitAsync(Deadline);
        Assert.That(healthy.ExtensionsSent, Is.True);
        Assert.That(healthy.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        Assert.That(healthy.Send(new Rpc().WithTopics(["wanted"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await healthy.Idle.Task.WaitAsync(Deadline);
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(sent.Last().Subscriptions[0].Topicid, Is.EqualTo("wanted"));
        Assert.That(disconnects, Is.Zero);
        Assert.That(healthy.Terminal, Is.False);
        Assert.That(router.OutboundUsage.Bytes, Is.EqualTo(optionalBytes));
        await router.DisposeAsync();
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MixedRpcsCannotCarryOptionalContentIntoTheRequiredReserve(bool bytes)
    {
        PubsubSettings settings = Settings();
        Rpc control = new() { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = "t" } } } };
        Rpc optional = Payload();
        optional.Control = new ControlMessage { Iwant = { new ControlIWant { MessageIDs = { ByteString.CopyFrom([1]) } } } };
        Rpc Mixed()
        {
            Rpc rpc = control.Clone();
            rpc.Publish.Add(optional.Publish);
            rpc.Control.Iwant.Add(optional.Control.Iwant);
            return rpc;
        }
        long split = FrameSize(control) + FrameSize(optional);
        // Optional traffic may occupy exactly one split optional frame beside one control frame.
        if (bytes) settings.MaxOutboundRpcBytes = 2L * split;
        else settings.MaxOutboundRpcEntries = 4;
        await using PubsubRouter router = new(new PeerStore(), settings);
        PausedWriter first = new(), second = new();
        PubsubRouter.PubsubPeer stalled = Open(router, first, 1, PubsubRouter.GossipsubProtocolVersionV11);
        PubsubRouter.PubsubPeer blocker = Open(router, second, 2, PubsubRouter.GossipsubProtocolVersionV11);
        Assert.That(stalled.Send(Mixed()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await first.Started.Task.WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((2, split)), "A mixed RPC is queued as a control frame and an optional frame.");
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        Assert.That(blocker.Send(Mixed()), Is.EqualTo(PubsubRouter.SendResult.Dropped), "Optional content drops at optional capacity; its control is queued.");
        await second.Started.Task.WaitAsync(Deadline);
        Assert.That(blocker.Terminal, Is.False);
        Assert.That(router.OutboundUsage, Is.EqualTo((3, split + FrameSize(control))));
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1), "A dropped request creates no promise.");
        ConcurrentQueue<Rpc> sent = [];
        int disconnects = 0;
        PubsubRouter.PubsubPeer healthy = router.OutboundConnection(TestPeers.Multiaddr(3), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, (frame, _) =>
            {
                sent.Enqueue(RouterTestExtensions.Decode(frame));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; })!;
        Assert.That(healthy.Send(Mixed()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        await healthy.Idle.Task.WaitAsync(Deadline);
        Assert.That(sent.Select(rpc => (rpc.Control?.Prune.Count ?? 0, rpc.Publish.Count, rpc.Control?.Iwant.Count ?? 0)),
            Is.EqualTo(new[] { (1, 0, 0) }), "Only the control frame is written; payload never rides on it.");
        Assert.That(healthy.Send(new Rpc().WithTopics(["wanted"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await healthy.Idle.Task.WaitAsync(Deadline);
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(disconnects, Is.Zero);
        Assert.That(healthy.Terminal, Is.False);
        Assert.That(healthy.Score.BehaviorPenalty, Is.Zero);
        Assert.That(blocker.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        Assert.That(router.OutboundUsage, Is.EqualTo((3, split + FrameSize(control))), "Stalled frames stay charged; healthy writes release theirs.");
        first.Result.SetResult(IOResult.Ok);
        second.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PruneAndIwantResponseToOneRpcSplitSoTheReplyNeverEntersTheReserve(bool saturated)
    {
        PubsubSettings settings = Settings();
        settings.Degree = settings.LowestDegree = settings.HighestDegree = 1;
        settings.DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign;
        settings.GetMessageId = message => new MessageId(message.Data.ToByteArray());
        // Optional traffic may hold one entry; required control may use both.
        if (saturated) settings.MaxOutboundRpcEntries = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        PausedWriter stalledWriter = new();
        PubsubRouter.PubsubPeer stalled = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, (frame, token) => RouterTestExtensions.Decode(frame).Subscriptions.Count != 0
                ? ValueTask.FromResult(IOResult.Ok) : stalledWriter.Write(frame, token), stalledWriter.Abort, stalledWriter.Disconnect)!;
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        if (saturated)
        {
            Assert.That(stalled.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
            await stalledWriter.Started.Task.WaitAsync(Deadline);
        }
        PubsubRouter.PubsubPeer meshPeer = router.OutboundConnection(TestPeers.Multiaddr(2), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, (_, _) => ValueTask.FromResult(IOResult.Ok), () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
        await router.OnRpcAsync(meshPeer.PeerId, new Rpc().WithTopics(["topic"], []));
        await router.OnRpcAsync(meshPeer.PeerId, new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "topic" } } } });
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Is.EqualTo(new[] { meshPeer.PeerId }));
        await router.OnRpcAsync(meshPeer.PeerId, new Rpc { Publish = { new Message { Topic = "topic", Data = ByteString.CopyFrom([7]) } } });
        // The mesh peer's hello must have released its entry before the requester's required hello needs it.
        await meshPeer.Idle.Task.WaitAsync(Deadline);
        ConcurrentQueue<Rpc> sent = [];
        int disconnects = 0;
        PubsubRouter.PubsubPeer requester = router.OutboundConnection(TestPeers.Multiaddr(3), PubsubRouter.GossipsubProtocolVersionV10,
            new TaskCompletionSource().Task, (frame, _) =>
            {
                sent.Enqueue(RouterTestExtensions.Decode(frame));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; })!;
        await router.OnRpcAsync(requester.PeerId, new Rpc().WithTopics(["topic"], []));
        await requester.Idle.Task.WaitAsync(Deadline);
        Assert.That(sent.Select(rpc => rpc.Subscriptions.Count), Is.EqualTo(new[] { 1 }), "The hello precedes the reply.");
        // A full mesh answers GRAFT with PRUNE; the IWANT reply is optional payload in the same response RPC.
        await router.OnRpcAsync(requester.PeerId, new Rpc
        {
            Control = new ControlMessage
            {
                Graft = { new ControlGraft { TopicID = "topic" } },
                Iwant = { new ControlIWant { MessageIDs = { ByteString.CopyFrom([7]) } } },
            },
        });
        await requester.Idle.Task.WaitAsync(Deadline);
        (int Prune, int Publish)[] replies = sent.Skip(1).Select(rpc => (rpc.Control?.Prune.Count ?? 0, rpc.Publish.Count)).ToArray();
        Assert.That(replies, Is.EqualTo(saturated ? new[] { (1, 0) } : new[] { (1, 0), (0, 1) }));
        Assert.That(requester.Terminal, Is.False);
        Assert.That(disconnects, Is.Zero);
        Assert.That(requester.Score.BehaviorPenalty, Is.Zero);
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Is.EqualTo(new[] { meshPeer.PeerId }));
        Assert.That(router.OutboundUsage, Is.EqualTo(saturated ? (1, (long)FrameSize(Payload())) : (0, 0L)));
        stalledWriter.Result.TrySetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task SubscribeBurstCoalescesQueuedControlInsteadOfClosingHealthyPeers()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        RecordingGate gate = new();
        PubsubRouter.PubsubPeer busy = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, gate.Write, gate.Abort, gate.Disconnect)!;
        int replyDisconnects = 0;
        // The reply dial completes without attaching a writer, so nothing drains this peer until OutboundConnection runs.
        router.InboundConnection(TestPeers.Multiaddr(2), PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
            () => Task.CompletedTask, () => { Interlocked.Increment(ref replyDisconnects); return Task.CompletedTask; });
        var peers = (ConcurrentDictionary<PeerId, PubsubRouter.PubsubPeer>)typeof(PubsubRouter)
            .GetField("peerState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
        PubsubRouter.PubsubPeer unattached = peers[TestPeers.PeerId(2)];
        int burst = 2 * Settings().MaxOutboundRpcEntriesPerPeer;
        // The first announcement is being written before the burst, so the burst can only queue behind it.
        router.Subscribe("topic-000");
        await gate.Started.Task.WaitAsync(Deadline);
        for (int i = 1; i < burst; i++) router.Subscribe($"topic-{i:D3}");
        Assert.That(busy.Terminal, Is.False);
        Assert.That(unattached.Terminal, Is.False);
        Assert.That(busy.Entries, Is.EqualTo(2), "One frame is being written; every later subscription merged into one pending frame.");
        Assert.That(unattached.Entries, Is.EqualTo(1), "Nothing drains before the reply stream attaches, so the whole burst is one frame.");
        Assert.That(router.OutboundUsage.Entries, Is.EqualTo(3));
        gate.First.SetResult(IOResult.Ok);
        await busy.Idle.Task.WaitAsync(Deadline);
        string[] expected = Enumerable.Range(0, burst).Select(i => $"topic-{i:D3}").ToArray();
        Assert.That(gate.Frames, Has.Count.EqualTo(2));
        Assert.That(gate.Frames[0].Subscriptions.Select(sub => sub.Topicid), Is.EqualTo(new[] { "topic-000" }));
        Assert.That(gate.Frames.SelectMany(rpc => rpc.Subscriptions).Select(sub => sub.Topicid), Is.EqualTo(expected), "Order is preserved across the merge.");
        Assert.That(gate.Frames.SelectMany(rpc => rpc.Subscriptions).All(sub => sub.Subscribe), Is.True);
        List<Rpc> attached = [];
        Assert.That(router.OutboundConnection(TestPeers.Multiaddr(2), PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
            (frame, _) => { attached.Add(RouterTestExtensions.Decode(frame)); return ValueTask.FromResult(IOResult.Ok); },
            () => ValueTask.CompletedTask, () => Task.CompletedTask), Is.SameAs(unattached));
        await unattached.Idle.Task.WaitAsync(Deadline);
        // Attaching the writer also sends the current subscription snapshot; same-topic control never merges.
        Assert.That(attached, Has.Count.EqualTo(2));
        Assert.That(attached[0].Subscriptions.Select(sub => sub.Topicid), Is.EqualTo(expected), "The whole burst left as one ordered frame.");
        Assert.That(attached[1].Subscriptions.Select(sub => sub.Topicid), Is.EquivalentTo(expected), "The hello snapshot follows it.");
        Assert.That(gate.Disconnects + replyDisconnects, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task CoalescingKeepsPerTopicOrderOwnsItsBytesAndChargesTheDelta()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        RecordingGate gate = new();
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, gate.Write, gate.Abort, gate.Disconnect)!;
        static Rpc Graft(string topic) => new() { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = topic } } } };
        static Rpc Prune(string topic, ulong backoff) => new() { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = topic, Backoff = backoff } } } };
        Assert.That(peer.Send(new Rpc().WithTopics(["first"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await gate.Started.Task.WaitAsync(Deadline);
        Rpc caller = Graft("t");
        Assert.That(peer.Send(caller), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        caller.Control.Graft[0].TopicID = "mutated-after-admission";
        long afterGraft = router.OutboundUsage.Bytes;
        Assert.That(peer.Send(Graft("t")), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(2), "A repeated GRAFT is redundant.");
        Assert.That(router.OutboundUsage.Bytes, Is.EqualTo(afterGraft));
        Assert.That(peer.Send(Prune("t", 1)), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(3), "PRUNE for a topic with a pending GRAFT keeps its own frame.");
        Assert.That(peer.Send(Prune("t", 2)), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(3), "A repeated PRUNE is superseded.");
        Assert.That(peer.Send(new Rpc().WithTopics(["t"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(4), "A subscription for a topic with pending control keeps its own frame.");
        Assert.That(peer.Send(new Rpc().WithTopics([], ["u"])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(4));
        Assert.That(peer.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Send(Graft("v")), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(6), "Control behind a queued payload starts a new frame to keep FIFO order.");
        gate.First.SetResult(IOResult.Ok);
        await peer.Idle.Task.WaitAsync(Deadline);
        Assert.That(gate.Frames, Has.Count.EqualTo(6));
        Assert.That(gate.Frames[1].Control.Graft.Select(g => g.TopicID), Is.EqualTo(new[] { "t" }), "The merged frame owns its content; the caller's later mutation is invisible.");
        Assert.That(gate.Frames[2].Control.Prune.Select(p => (p.TopicID, p.Backoff)), Is.EqualTo(new[] { ("t", 2UL) }));
        Assert.That(gate.Frames[3].Subscriptions.Select(s => (s.Topicid, s.Subscribe)), Is.EqualTo(new[] { ("t", true), ("u", false) }));
        Assert.That(gate.Frames[4].Publish, Has.Count.EqualTo(1));
        Assert.That(gate.Frames[5].Control.Graft.Select(g => g.TopicID), Is.EqualTo(new[] { "v" }));
        Assert.That(gate.FrameBytes, Is.EqualTo(gate.Frames.Sum(FrameSize)), "Charged bytes matched the re-serialized frames.");
        Assert.That(peer.Terminal, Is.False);
        Assert.That(gate.Disconnects, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task OneEntryGlobalBudgetIsReservedForRequiredControl()
    {
        PubsubSettings settings = Settings();
        settings.MaxOutboundRpcEntries = 1;
        await using PubsubRouter router = new(new PeerStore(), settings);
        PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = Open(router, writer);
        Assert.That(peer.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
        Assert.That(peer.Send(new Rpc().WithTopics(["wanted"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(router.OutboundUsage.Entries, Is.EqualTo(1));
        writer.Result.SetResult(IOResult.Ok);
        await peer.Idle.Task.WaitAsync(Deadline);
        Assert.That(peer.Terminal, Is.False);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [TestCase(IOResult.Ended)]
    [TestCase(IOResult.Cancelled)]
    [TestCase(IOResult.InternalError)]
    [TestCase(IOResult.Aborted)]
    [TestCase(null)]
    public async Task UnsuccessfulWritesTerminateAndCancelPromises(IOResult? result)
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = Open(router, writer, protocol: PubsubRouter.GossipsubProtocolVersionV11);
        peer.Send(Iwant());
        await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        if (result is null) writer.Result.SetException(new IOException("failed write"));
        else writer.Result.SetResult(result.Value);
        await peer.Completion.WaitAsync(Deadline);
        await router.Heartbeat();
        Assert.That(peer.Terminal, Is.True);
        Assert.That(writer.Disconnects, Is.EqualTo(1));
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.IwantPromiseCount, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task OversizedOptionalFrameDropsAndFirstExtensionRejectionDoesNotClaimItWasSent()
    {
        PubsubSettings settings = Settings();
        settings.EnablePartialMessages = true;
        settings.MaxOutboundRpcBytesPerPeer = 1;
        await using PubsubRouter router = new(new PeerStore(), settings);
        PausedWriter optional = new();
        PubsubRouter.PubsubPeer peer = Open(router, optional);
        Assert.That(peer.Send(Payload()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        Assert.That(peer.Terminal, Is.False);
        PausedWriter extension = new();
        PubsubRouter.PubsubPeer extended = Open(router, extension, 2, PubsubRouter.GossipsubProtocolVersionV13);
        await extended.Completion.WaitAsync(Deadline);
        Assert.That(extended.ExtensionsSent, Is.False);
        Assert.That(extended.ExtensionsQueued, Is.False);
        Assert.That(extension.Writes, Is.Zero);
        Assert.That(extension.Disconnects, Is.EqualTo(1));
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task DuplicateOutboundRegistrationDoesNotStopTheEstablishedWriter()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = Open(router, writer);
        PausedWriter duplicate = new();
        Assert.That(router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            Task.CompletedTask, duplicate.Write, duplicate.Abort, duplicate.Disconnect), Is.Null);
        peer.Send(Payload());
        await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(peer.Terminal, Is.False);
        Assert.That(writer.Disconnects, Is.Zero);
        writer.Result.SetResult(IOResult.Ok);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
    }

    [Test]
    public async Task FilteringAndExtensionAdvertisementDoNotMutateTheSharedRpc()
    {
        PubsubSettings settings = Settings();
        settings.EnablePartialMessages = true;
        await using PubsubRouter router = new(new PeerStore(), settings);
        ConcurrentQueue<Rpc> oldSent = [], newSent = [];
        PubsubRouter.PubsubPeer OldOrNew(int peer, string protocol, ConcurrentQueue<Rpc> sent) =>
            router.OutboundConnection(TestPeers.Multiaddr(peer), protocol, new TaskCompletionSource().Task,
                (frame, _) => { sent.Enqueue(RouterTestExtensions.Decode(frame)); return ValueTask.FromResult(IOResult.Ok); },
                () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
        PubsubRouter.PubsubPeer oldPeer = OldOrNew(1, PubsubRouter.GossipsubProtocolVersionV12, oldSent);
        PubsubRouter.PubsubPeer newPeer = OldOrNew(2, PubsubRouter.GossipsubProtocolVersionV13, newSent);
        await router.FlushOutboundAsync();
        Rpc shared = new() { Subscriptions = { new Rpc.Types.SubOpts { Topicid = "t", Subscribe = true, RequestsPartial = true, SupportsSendingPartial = true } } };
        Rpc expected = shared.Clone();
        oldPeer.Send(shared);
        newPeer.Send(shared);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(shared, Is.EqualTo(expected));
        Assert.That(oldSent.Last().Subscriptions[0].HasRequestsPartial, Is.False);
        Assert.That(newSent.Last().Subscriptions[0].RequestsPartial, Is.True);
        Assert.That(newSent.Count(rpc => rpc.Control?.Extensions is not null), Is.EqualTo(1));
        Assert.That(newPeer.ExtensionsSent, Is.True);
    }

    [Test]
    public async Task DisposalAbortsAPartlyConsumedRealChannelBeforeReleasingItsReservation()
    {
        PubsubRouter router = new(new PeerStore(), Settings());
        TestChannel channel = new();
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, (frame, token) => channel.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(frame), token),
            channel.AbortAsync, () => Task.CompletedTask)!;
        Rpc rpc = Payload(4096);
        peer.Send(rpc);
        ReadResult read = await channel.Reverse().ReadAsync(1, token: Deadline);
        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(router.OutboundUsage, Is.EqualTo((1, (long)FrameSize(rpc))));
        await router.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.That(peer.Completion.IsCompleted, Is.True);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task RegistrationRacingDisposalCannotLeaveAnOwnedWriterRunning()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            PubsubRouter router = new(new PeerStore(), Settings());
            using ManualResetEventSlim start = new();
            PubsubRouter.PubsubPeer? peer = null;
            Task register = Task.Run(() =>
            {
                start.Wait(Deadline);
                peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
                    new TaskCompletionSource().Task, (_, _) => ValueTask.FromResult(IOResult.Ok),
                    () => ValueTask.CompletedTask, () => Task.CompletedTask);
                peer?.Send(Payload());
            });
            Task stop = Task.Run(async () =>
            {
                start.Wait(Deadline);
                await router.DisposeAsync();
            });
            start.Set();
            await Task.WhenAll(register, stop).WaitAsync(Deadline);
            await router.DisposeAsync();
            Assert.That(peer is null || peer.Completion.IsCompleted, Is.True);
            Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
        }
    }

    [Test]
    public async Task ClosingADuplicateInboundStreamDoesNotRemoveTheEstablishedGeneration()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        PubsubRouter.PubsubPeer peer = Open(router, new PausedWriter());
        router.InboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task,
            () => Task.CompletedTask, () => Task.CompletedTask);
        TaskCompletionSource duplicate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.InboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion, duplicate.Task,
            () => Task.CompletedTask, () => Task.CompletedTask);
        duplicate.SetResult();
        while (true)
        {
            lock (router) { if (peer.IncomingStreams == 1) break; }
            await Task.Delay(1, Deadline);
        }
        Assert.That(peer.Terminal, Is.False);
        Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Has.Member(peer.PeerId));
    }

    [TestCase(nameof(PubsubSettings.MaxOutboundRpcEntriesPerPeer))]
    [TestCase(nameof(PubsubSettings.MaxOutboundRpcBytesPerPeer))]
    [TestCase(nameof(PubsubSettings.MaxOutboundRpcEntries))]
    [TestCase(nameof(PubsubSettings.MaxOutboundRpcBytes))]
    [TestCase(nameof(PubsubSettings.OutboundRpcTimeout))]
    [TestCase(nameof(PubsubSettings.MaxIncomingMessageIdRpcEntries))]
    [TestCase(nameof(PubsubSettings.MaxIncomingMessageIdRpcBytes))]
    [TestCase(nameof(PubsubSettings.MaxIncomingMessageIdRpcEntriesPerPeer))]
    [TestCase(nameof(PubsubSettings.MaxIncomingMessageIdRpcBytesPerPeer))]
    public void CapacitiesArePositiveAtConstruction(string name)
    {
        PubsubSettings settings = Settings();
        var property = typeof(PubsubSettings).GetProperty(name)!;
        foreach (int invalid in new[] { -1, 0 })
        {
            property.SetValue(settings, Convert.ChangeType(invalid, property.PropertyType));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), settings));
        }
    }

    internal static PubsubRouter.PubsubPeer Open(PubsubRouter router, PausedWriter writer, int index = 1,
        string protocol = PubsubRouter.FloodsubProtocolVersion) =>
        router.OutboundConnection(TestPeers.Multiaddr(index), protocol, new TaskCompletionSource().Task,
            writer.Write, writer.Abort, writer.Disconnect)!;

    internal static Rpc Iwant() => new() { Control = new ControlMessage { Iwant = { new ControlIWant { MessageIDs = { ByteString.CopyFrom([1]) } } } } };

    internal sealed class RecordingGate
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IOResult> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<Rpc> Frames = [];
        public long FrameBytes;
        public int Disconnects;
        public ValueTask<IOResult> Write(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            bool first;
            lock (Frames)
            {
                first = Frames.Count == 0;
                Frames.Add(RouterTestExtensions.Decode(bytes));
                FrameBytes += bytes.Length;
            }
            Started.TrySetResult();
            return first ? new(First.Task) : ValueTask.FromResult(IOResult.Ok);
        }
        // Teardown aborts the stream; completing the gated write keeps a failing test from parking the writer forever.
        public ValueTask Abort() { First.TrySetResult(IOResult.Aborted); return ValueTask.CompletedTask; }
        public Task Disconnect() { Interlocked.Increment(ref Disconnects); return Task.CompletedTask; }
    }

    internal sealed class PausedWriter
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IOResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReleaseOnAbort = true;
        public int Writes;
        public int Disconnects;
        public ValueTask<IOResult> Write(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            Interlocked.Increment(ref Writes);
            Started.TrySetResult();
            return new(Result.Task);
        }
        public ValueTask Abort()
        {
            Aborted.TrySetResult();
            if (ReleaseOnAbort) Result.TrySetResult(IOResult.Aborted);
            return ValueTask.CompletedTask;
        }
        public Task Disconnect() { Interlocked.Increment(ref Disconnects); return Task.CompletedTask; }
    }
}
