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
            if (global) settings.MaxOutboundRpcBytes = 2L * FrameSize(rpc);
            else settings.MaxOutboundRpcBytesPerPeer = 2L * FrameSize(rpc);
        }
        else if (global) settings.MaxOutboundRpcEntries = 2;
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
        if (bytes) settings.MaxOutboundRpcBytes = FrameSize(Payload());
        else settings.MaxOutboundRpcEntries = 1;
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
    [TestCase(nameof(PubsubSettings.MaxIncomingMessageIdRpcEntries))]
    [TestCase(nameof(PubsubSettings.MaxIncomingMessageIdRpcBytes))]
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
