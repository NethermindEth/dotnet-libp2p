// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Metrics;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class IncomingAdmissionTests
{
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;
    private static SemaphoreSlim Slots(PubsubRouter router) => (SemaphoreSlim)typeof(PubsubRouter)
        .GetField("_messageIdCalculations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
    private static Rpc Message(byte value = 1) => new() { Publish = { new Message { Topic = "wanted", Data = ByteString.CopyFrom([value]) } } };
    private static PubsubSettings Settings() => new()
    {
        AutoConnect = false,
        HeartbeatInterval = int.MaxValue,
        DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        MaxConcurrentMessageIdCalculations = 1,
        GetMessageId = message => new MessageId(message.Data.ToByteArray()),
    };
    private static PubsubRouter.PubsubPeer Connect(PubsubRouter router, int index) =>
        router.OutboundConnection(TestPeers.Multiaddr(index), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task,
            (_, _) => ValueTask.FromResult(IOResult.Ok), () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
    private static ISessionContext Context(int index)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(index) });
        context.DialAsync(Arg.Any<ISessionProtocol>()).Returns(Task.CompletedTask);
        return context;
    }
    private static MeterListener Skips(Action<string> onSkip)
    {
        MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == Libp2pMetrics.MeterName && instrument.Name == "libp2p.pubsub.incoming.skipped")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "reason") for (long i = 0; i < value; i++) onSkip((string)tag.Value!);
            }
        });
        listener.Start();
        return listener;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SaturationSkipsPublishesButStillHandlesControls(bool bytes)
    {
        PubsubSettings settings = Settings();
        settings.MaxIncomingMessageIdRpcEntries = bytes ? 100 : 2;
        settings.MaxIncomingMessageIdRpcBytes = bytes ? 2L * Message().CalculateSize() : 1024;
        int calculations = 0, deliveries = 0, validations = 0;
        settings.GetMessageId = message => { calculations++; return new MessageId(message.Data.ToByteArray()); };
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("wanted");
        router.OnMessage += (_, _, _) => deliveries++;
        router.VerifyMessage = (_, _) => { validations++; return MessageValidity.Accepted; };
        router.OutboundConnection(TestPeers.Multiaddr(3), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task, _ => { });
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        Task first = router.OnRpcAsync(TestPeers.PeerId(1), Message());
        Task second = router.OnRpcAsync(TestPeers.PeerId(2), Message(2));
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        Assert.That(router.IncomingUsage, Is.EqualTo((2, 2L * Message().CalculateSize())));
        Rpc mixed = Message(3).WithTopics(["remote"], []);
        await router.OnRpcAsync(TestPeers.PeerId(3), mixed).WaitAsync(Deadline);
        Assert.That(((IRoutingStateContainer)router).FloodsubPeers["remote"], Has.Member(TestPeers.PeerId(3)));
        Assert.That(calculations + validations + deliveries, Is.Zero);
        Assert.That(router.IncomingUsage.Entries, Is.EqualTo(2));
        slots.Release();
        await Task.WhenAll(first, second).WaitAsync(Deadline);
        Assert.That((calculations, validations, deliveries), Is.EqualTo((2, 2, 2)));
        Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        await router.OnRpcAsync(TestPeers.PeerId(3), Message(3));
        Assert.That(deliveries, Is.EqualTo(3), "Overload must not cache the skipped message ID.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationReleasesAnAdmissionWaiterWithoutAcquiringASlot(bool stopRouter)
    {
        PubsubSettings settings = Settings();
        int calculations = 0;
        settings.GetMessageId = _ => { calculations++; return new MessageId([1]); };
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("wanted");
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        using CancellationTokenSource cancelled = new();
        Task receive = router.OnRpcAsync(TestPeers.PeerId(1), Message(), token: cancelled.Token);
        Assert.That(receive.IsCompleted, Is.False);
        Assert.That(router.IncomingUsage.Entries, Is.EqualTo(1));
        if (stopRouter) await router.DisposeAsync();
        else cancelled.Cancel();
        await receive.WaitAsync(Deadline);
        Assert.That(calculations, Is.Zero);
        Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        Assert.That(slots.CurrentCount, Is.Zero);
        slots.Release();
        if (!stopRouter)
        {
            await router.OnRpcAsync(TestPeers.PeerId(1), Message());
            Assert.That(calculations, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CalculatorExceptionReleasesBothBudgetsAndTheSemaphore()
    {
        PubsubSettings settings = Settings();
        settings.MaxIncomingMessageIdRpcEntries = 1;
        settings.GetMessageId = _ => throw new InvalidOperationException("calculator failed");
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("wanted");
        await router.OnRpcAsync(TestPeers.PeerId(1), Message());
        Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        Assert.That(Slots(router).CurrentCount, Is.EqualTo(1));
        int deliveries = 0;
        router.OnMessage += (_, _, _) => deliveries++;
        settings.GetMessageId = _ => new MessageId([1]);
        await router.OnRpcAsync(TestPeers.PeerId(1), Message());
        Assert.That(deliveries, Is.EqualTo(1));
    }

    [TestCase("unsubscribe")]
    [TestCase("graylist")]
    [TestCase("disconnect")]
    public async Task AdmissionRechecksInterestScoreAndGenerationAfterWaiting(string change)
    {
        PubsubSettings settings = Settings();
        int calculations = 0;
        settings.GetMessageId = _ => { calculations++; return new MessageId([1]); };
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("wanted");
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            closed.Task, (_, _) => ValueTask.FromResult(IOResult.Ok), () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        Task receive = router.OnRpcAsync(peer.PeerId, Message());
        Assert.That(router.IncomingUsage.Entries, Is.EqualTo(1));
        if (change == "unsubscribe") router.Unsubscribe("wanted");
        else if (change == "graylist") peer.Score.BehaviorPenalty = 1000;
        else
        {
            closed.SetResult();
            await peer.Completion.WaitAsync(Deadline);
        }
        slots.Release();
        await receive.WaitAsync(Deadline);
        Assert.That(calculations, Is.Zero);
        Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        Assert.That(slots.CurrentCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PerPeerShareKeepsOnePeerFromHoldingTheRouterBudget(bool bytes)
    {
        PubsubSettings settings = Settings();
        long size = Message().CalculateSize();
        if (bytes) settings.MaxIncomingMessageIdRpcBytesPerPeer = size;
        else settings.MaxIncomingMessageIdRpcEntriesPerPeer = 1;
        int calculations = 0, deliveries = 0;
        settings.GetMessageId = message => { calculations++; return new MessageId(message.Data.ToByteArray()); };
        List<string> skips = [];
        using MeterListener listener = Skips(reason => { lock (skips) skips.Add(reason); });
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("wanted");
        router.OnMessage += (_, _, _) => deliveries++;
        PubsubRouter.PubsubPeer greedy = Connect(router, 1), other = Connect(router, 2);
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        Task first = router.OnRpcAsync(greedy.PeerId, Message());
        Assert.That(first.IsCompleted, Is.False);
        Assert.That((greedy.IncomingEntries, greedy.IncomingBytes), Is.EqualTo((1, size)));
        // The second publish from the same peer exceeds its share: it completes without waiting for a slot.
        await router.OnRpcAsync(greedy.PeerId, Message(2)).WaitAsync(Deadline);
        Assert.That(router.IncomingUsage, Is.EqualTo((1, size)));
        Assert.That(skips, Is.EqualTo(new[] { "peer-capacity" }));
        Task third = router.OnRpcAsync(other.PeerId, Message(3));
        Assert.That(third.IsCompleted, Is.False, "Another peer's publish is still admitted.");
        Assert.That(router.IncomingUsage, Is.EqualTo((2, 2 * size)));
        Assert.That(calculations + deliveries, Is.Zero);
        slots.Release();
        await Task.WhenAll(first, third).WaitAsync(Deadline);
        Assert.That((calculations, deliveries), Is.EqualTo((2, 2)));
        Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        Assert.That((greedy.IncomingEntries, greedy.IncomingBytes, other.IncomingEntries, other.IncomingBytes), Is.EqualTo((0, 0L, 0, 0L)));
        await router.OnRpcAsync(greedy.PeerId, Message(2));
        Assert.That(deliveries, Is.EqualTo(3), "A skipped message ID is not cached.");
        Assert.That(skips, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RegisteredStreamsOfOnePeerShareItsIncomingAllowance()
    {
        PubsubSettings settings = Settings();
        settings.MaxIncomingMessageIdRpcEntriesPerPeer = 1;
        int deliveries = 0;
        await using PubsubRouter router = new(new PeerStore(), settings);
        await router.StartAsync(new LocalPeerStub());
        router.GetTopic("wanted");
        router.OnMessage += (_, _, _) => Interlocked.Increment(ref deliveries);
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        TestChannel first = new(), duplicate = new(), other = new();
        Task firstListen = new FloodsubProtocol(router).ListenAsync(first, Context(1));
        Task duplicateListen = new FloodsubProtocol(router).ListenAsync(duplicate, Context(1));
        Task otherListen = new FloodsubProtocol(router).ListenAsync(other, Context(2));
        try
        {
            await first.Reverse().WriteSizeAndProtobufAsync(Message());
            while (router.IncomingUsage.Entries == 0) await Task.Delay(1, Deadline);
            // The same peer's second stream exceeds its share: its publish is skipped and its loop keeps reading.
            // A write parks until the loop reads it, so bound it: an admitted publish would park this frame.
            await duplicate.Reverse().WriteSizeAndProtobufAsync(Message(2));
            await duplicate.Reverse().WriteSizeAndProtobufAsync(new Rpc().WithTopics(["later"], [])).AsTask().WaitAsync(Deadline);
            while (!((IRoutingStateContainer)router).FloodsubPeers.ContainsKey("later")) await Task.Delay(1, Deadline);
            Assert.That(router.IncomingUsage.Entries, Is.EqualTo(1));
            await other.Reverse().WriteSizeAndProtobufAsync(Message(3));
            while (router.IncomingUsage.Entries < 2) await Task.Delay(1, Deadline);
            Assert.That(router.IncomingUsage.Entries, Is.EqualTo(2), "Another peer's stream is still admitted.");
            Assert.That(Volatile.Read(ref deliveries), Is.Zero);
            slots.Release();
            while (router.IncomingUsage.Entries != 0) await Task.Delay(1, Deadline);
            Assert.That(Volatile.Read(ref deliveries), Is.EqualTo(2));
            Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        }
        finally
        {
            if (slots.CurrentCount == 0) slots.Release();
            await first.AbortAsync();
            await duplicate.AbortAsync();
            await other.AbortAsync();
            await Task.WhenAll(firstListen, duplicateListen, otherListen).WaitAsync(Deadline);
        }
    }

    [Test]
    public async Task RegisteredListenerAwaitsAdmissionWithoutReadingAheadAndOtherStreamsProgress()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        await router.StartAsync(new LocalPeerStub());
        router.GetTopic("wanted");
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        TestChannel first = new(), second = new();
        Task firstListen = new FloodsubProtocol(router).ListenAsync(first, Context(1));
        Task secondListen = new FloodsubProtocol(router).ListenAsync(second, Context(2));
        try
        {
            await first.Reverse().WriteSizeAndProtobufAsync(Message());
            while (router.IncomingUsage.Entries == 0) await Task.Delay(1, Deadline);
            Assert.That(router.IncomingUsage.Entries, Is.EqualTo(1), "The listener reached the occupied admission gate.");
            Task nextFrame = first.Reverse().WriteSizeAndProtobufAsync(new Rpc().WithTopics(["later"], [])).AsTask();
            await second.Reverse().WriteSizeAndProtobufAsync(new Rpc().WithTopics(["other"], []));
            while (!((IRoutingStateContainer)router).FloodsubPeers.ContainsKey("other")) await Task.Delay(1, Deadline);
            Assert.That(nextFrame.IsCompleted, Is.False, "The first listener cannot consume the next frame while awaiting admission.");
            Assert.That(router.IncomingUsage.Entries, Is.EqualTo(1));
            slots.Release();
            await nextFrame.WaitAsync(Deadline);
            await first.Reverse().WriteEofAsync(Deadline);
            await firstListen.WaitAsync(Deadline);
            Assert.That(router.IncomingUsage, Is.EqualTo((0, 0L)));
        }
        finally
        {
            if (slots.CurrentCount == 0) slots.Release();
            await first.AbortAsync();
            await second.AbortAsync();
            await Task.WhenAll(firstListen, secondListen).WaitAsync(Deadline);
        }
    }
}
