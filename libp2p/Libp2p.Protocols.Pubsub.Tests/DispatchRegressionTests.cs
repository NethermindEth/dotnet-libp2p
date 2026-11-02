// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class DispatchRegressionTests
{
    [Test]
    public async Task StalledProductionWriterHasOnlyOneActiveWrite()
    {
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false });
        await router.StartAsync(new LocalPeerStub());
        router.GetTopic("topic");
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        CountingChannel channel = new();
        Task dial = new FloodsubProtocol(router).DialAsync(channel, context);
        await channel.Started.Task.WaitAsync(TestContext.CurrentContext.CancellationToken);
        await router.OnRpcAsync(TestPeers.PeerId(2), new Rpc().WithTopics(["topic"], []));
        for (int i = 0; i < 100; i++) router.Publish("topic", new byte[4096]);
        int writes = channel.Writes;
        await channel.AbortAsync();
        await dial;
        Assert.That(writes, Is.EqualTo(1), "A stalled channel must have a single owned write.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ForwardsBeforeInvokingSynchronousSubscriber(bool deferred)
    {
        using PubsubRouter router = new(new PeerStore());
        router.GetTopic("topic");
        List<Rpc> forwarded = [];
        router.OutboundConnection(TestPeers.Multiaddr(2), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task, forwarded.Add);
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["topic"], []));
        int forwardsAtCallback = -1;
        router.OnMessage += (_, _, _) =>
        {
            router.FlushOutboundAsync().GetAwaiter().GetResult();
            forwardsAtCallback = forwarded.Sum(rpc => rpc.Publish.Count);
        };
        Identity author = TestPeers.Identity(3);
        Rpc incoming = new Rpc().WithMessages("topic", 1, author.PeerId.Bytes, [1], author);
        TaskCompletionSource validation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (deferred)
        {
            router.VerifyMessage = (_, _) => MessageValidity.Deferred;
            router.OnDeferredMessage = (_, _) => validation.Task;
        }
        try
        {
            router.OnRpc(TestPeers.PeerId(1), incoming);
            if (deferred) Assert.That(router.CompleteValidation(incoming.Publish[0], MessageValidity.Accepted), Is.True);
            Assert.That(forwardsAtCallback, Is.EqualTo(1));
        }
        finally { validation.TrySetResult(); }
    }

    [Test]
    public async Task IncomingAdmissionReturnsAnIncompleteTaskWithoutBlockingCaller()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { MaxConcurrentMessageIdCalculations = 1 });
        router.GetTopic("topic");
        SemaphoreSlim semaphore = (SemaphoreSlim)typeof(PubsubRouter).GetField("_messageIdCalculations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
        await semaphore.WaitAsync();
        TaskCompletionSource<Task> returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Identity author = TestPeers.Identity(3);
        Rpc rpc = new Rpc().WithMessages("topic", 1, author.PeerId.Bytes, [1], author);
        Thread caller = new(() =>
        {
            returned.SetResult(router.OnRpcAsync(TestPeers.PeerId(1), rpc));
        });
        caller.Start();
        Assert.That(SpinWait.SpinUntil(() => returned.Task.IsCompleted || (caller.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000), Is.True);
        bool yielded = returned.Task.IsCompleted;
        bool pending = yielded && !returned.Task.Result.IsCompleted;
        semaphore.Release();
        caller.Join();
        await await returned.Task;
        Assert.That(yielded && pending, Is.True, "The receive caller must yield while admission is occupied.");
    }

    [Test]
    public void PartialSubscriberCanWaitForForwardingFromTheSameRpc()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions { RequestPartialMessages = true, SupportsSendingPartialMessages = true });
        List<Rpc> forwarded = [];
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, new TaskCompletionSource().Task, _ => { });
        router.OutboundConnection(TestPeers.Multiaddr(2), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task, forwarded.Add);
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["topic"], []));
        int forwardsAtCallback = -1;
        router.OnPartialMessage += (_, _, _) =>
        {
            router.FlushOutboundAsync().GetAwaiter().GetResult();
            forwardsAtCallback = forwarded.Sum(rpc => rpc.Publish.Count);
        };
        Identity author = TestPeers.Identity(3);
        Rpc incoming = new Rpc().WithMessages("topic", 1, author.PeerId.Bytes, [1], author);
        incoming.Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } };
        incoming.Partial = new PartialMessagesExtension { TopicID = ByteString.CopyFromUtf8("topic"), GroupID = ByteString.CopyFrom([1]), PartialMessage = ByteString.CopyFrom([2]) };
        router.OnRpc(TestPeers.PeerId(1), incoming);
        Assert.That(forwardsAtCallback, Is.EqualTo(1));
    }

    private sealed class CountingChannel : IChannel
    {
        private readonly TestChannel inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes);
            ValueTask<IOResult> result = inner.WriteAsync(bytes, token);
            Started.TrySetResult();
            return result;
        }
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default) => inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }
}
