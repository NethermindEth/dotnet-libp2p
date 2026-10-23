// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
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

    [Test]
    public async Task RegisteredListenerAwaitsAdmissionWithoutReadingAheadAndOtherStreamsProgress()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        await router.StartAsync(new LocalPeerStub());
        router.GetTopic("wanted");
        SemaphoreSlim slots = Slots(router);
        await slots.WaitAsync(Deadline);
        TestChannel first = new(), second = new();
        ISessionContext Context(int index)
        {
            ISessionContext context = Substitute.For<ISessionContext>();
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(index) });
            context.DialAsync(Arg.Any<ISessionProtocol>()).Returns(Task.CompletedTask);
            return context;
        }
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
