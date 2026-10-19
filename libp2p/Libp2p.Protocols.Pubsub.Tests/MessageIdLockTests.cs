// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

/// <remarks>
/// <see cref="PubsubSettings.GetMessageId"/> is supplied by the host and can be expensive: Ethereum
/// beacon gossip decompresses Snappy and hashes the uncompressed payload. Running it under the
/// router lock stalls every other peer, so it runs before the lock is taken and its result is
/// carried through duplicate detection, scoring and logging instead of being recomputed.
/// </remarks>
[TestFixture]
public class MessageIdLockTests
{
    private const string Topic = "message-id";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public void AnExpensiveMessageIdDoesNotStallOtherPeers()
    {
        using ManualResetEventSlim calculating = new(false);
        using ManualResetEventSlim release = new(false);
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            if (message.Data.Span[0] == 1)
            {
                calculating.Set();
                // No timeout: only the explicit release below lets this finish, so the assertions
                // cannot be satisfied by the calculation quietly completing on its own.
                release.Wait();
            }

            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);

        Task slowPeer = Task.Run(() => router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1))));
        try
        {
            Assert.That(calculating.Wait(Timeout), Is.True, "The message id calculation never started.");

            Task otherPeer = Task.Run(() => router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics([Topic], [])));

            Assert.Multiple(() =>
            {
                Assert.That(otherPeer.Wait(Timeout), Is.True,
                    "A control-only RPC from another peer must not wait on an unrelated peer's message id calculation.");
                Assert.That(release.IsSet, Is.False,
                    "It must complete while the calculation is still running, not after it was released.");
            });
        }
        finally
        {
            release.Set();
            Assert.That(slowPeer.Wait(Timeout), Is.True);
        }
    }

    [Test]
    public void AReceivedMessageIsIdentifiedOnce()
    {
        int calculations = 0;
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            Interlocked.Increment(ref calculations);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Has.Count.EqualTo(1), "The message must still be delivered.");
            Assert.That(calculations, Is.EqualTo(1),
                "Duplicate detection, scoring and logging must reuse the id rather than recompute it.");
        });
    }

    [Test]
    public void AKnownMessageIsIdentifiedOncePerDelivery()
    {
        int calculations = 0;
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            Interlocked.Increment(ref calculations);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        int afterFirst = calculations;
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.That(calculations - afterFirst, Is.EqualTo(1),
            "A duplicate costs one calculation to recognize it, and nothing more.");
    }

    [Test]
    public void AGraylistedPeerDoesNotReachTheCalculation()
    {
        int calculations = 0;
        PubsubSettings settings = Settings();
        // One rejected delivery scores -0.5, so graylist just above it rather than driving the
        // score down to the -100 default.
        settings.GraylistThreshold = -0.25;
        settings.GetMessageId = message =>
        {
            Interlocked.Increment(ref calculations);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);

        router.VerifyMessage = (_, _) => MessageValidity.Rejected;
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        int afterRejection = calculations;

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(2)));

        Assert.That(calculations, Is.EqualTo(afterRejection),
            "A graylisted peer must be rejected before the expensive message id calculation runs.");
    }

    [Test]
    public void PublishingDoesNotStallOtherPeers()
    {
        using ManualResetEventSlim calculating = new(false);
        using ManualResetEventSlim release = new(false);
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            if (message.Data.Span[0] == 9)
            {
                calculating.Set();
                release.Wait();
            }

            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ILocalPeer local = Substitute.For<ILocalPeer>();
        local.ListenAddresses.Returns([TestPeers.Multiaddr(9)]);
        local.Identity.Returns(TestPeers.Identity(9));
        _ = router.StartAsync(local);
        ConnectPeers(router);

        Task publishing = Task.Run(() => router.Publish(Topic, [9]));
        try
        {
            Assert.That(calculating.Wait(Timeout), Is.True, "The message id calculation never started.");

            Task otherPeer = Task.Run(() => router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([Topic], [])));

            Assert.Multiple(() =>
            {
                Assert.That(otherPeer.Wait(Timeout), Is.True,
                    "A peer's RPC must not wait on a local publish identifying its own message.");
                Assert.That(release.IsSet, Is.False,
                    "It must complete while the calculation is still running, not after it was released.");
            });
        }
        finally
        {
            release.Set();
            Assert.That(publishing.Wait(Timeout), Is.True);
        }
    }

    [Test]
    public void APeerThatDisconnectsDuringTheCalculationIsNotAccepted()
    {
        using ManualResetEventSlim calculating = new(false);
        using ManualResetEventSlim release = new(false);
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            calculating.Set();
            release.Wait();
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        TaskCompletionSource closed = new();
        Multiaddress address = TestPeers.Multiaddr(1);
        router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion, closed.Task, _ => { });
        router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics([Topic], []));
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);

        Task sender = Task.Run(() => router.OnRpc(address.GetPeerId()!, RpcWith(NewMessage(1))));
        try
        {
            Assert.That(calculating.Wait(Timeout), Is.True, "The message id calculation never started.");

            closed.SetResult();
            Assert.That(
                SpinWait.SpinUntil(
                    () => !((IRoutingStateContainer)router).ConnectedPeers.Contains(address.GetPeerId()!), Timeout),
                Is.True,
                "The peer was never removed.");
        }
        finally
        {
            release.Set();
            Assert.That(sender.Wait(Timeout), Is.True);
        }

        Assert.That(deliveries, Is.Empty,
            "A message whose sender disconnected while its id was being calculated must not be accepted.");
    }

    [Test]
    public async Task DisposingTheRouterDoesNotStrandAQueuedCalculation()
    {
        using ManualResetEventSlim calculating = new(false);
        using ManualResetEventSlim release = new(false);
        PubsubSettings settings = Settings();
        settings.MaxConcurrentMessageIdCalculations = 1;
        settings.GetMessageId = message =>
        {
            if (message.Data.Span[0] == 1)
            {
                calculating.Set();
                release.Wait(Timeout);
            }

            return new MessageId(message.Data.ToByteArray());
        };

        PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);

        Task holder = Task.Run(() => router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1))));
        Assert.That(calculating.Wait(Timeout), Is.True, "The first calculation never started.");
        Task queued = Task.Run(() => router.OnRpc(TestPeers.PeerId(2), RpcWith(NewMessage(2))));
        // Let the second RPC reach the bound and park there, so disposal happens with a waiter
        // queued rather than before it arrives.
        Assert.That(await Task.WhenAny(queued, Task.Delay(TimeSpan.FromMilliseconds(250))), Is.Not.SameAs(queued),
            "The second RPC should be waiting on the bound.");

        router.Dispose();
        release.Set();

        Assert.Multiple(() =>
        {
            Assert.That(holder.Wait(Timeout), Is.True, "Disposal must not fault the running calculation.");
            Assert.That(queued.Wait(Timeout), Is.True, "Disposal must not strand the queued calculation.");
        });
    }

    [Test]
    public void AFailedCalculationReleasesItsSlot()
    {
        // A delegate that throws must not keep its slot: with the bound at one, a single bad
        // message would otherwise stop every later RPC from ever being identified.
        PubsubSettings settings = Settings();
        settings.MaxConcurrentMessageIdCalculations = 1;
        settings.GetMessageId = message => message.Data.Span[0] == 0
            ? throw new InvalidOperationException("message id calculation failed")
            : new MessageId(message.Data.ToByteArray());

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(0)));

        Task later = Task.Run(() => router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1))));
        Assert.Multiple(() =>
        {
            Assert.That(later.Wait(Timeout), Is.True, "The failed calculation never gave its slot back.");
            Assert.That(deliveries, Has.Count.EqualTo(1), "The message after the failure must still be delivered.");
        });
    }

    [Test]
    public void PublishingStaysWithinTheConfiguredBound()
    {
        const int publishers = 6;
        using CountdownEvent entered = new(1);
        using ManualResetEventSlim release = new(false);
        int active = 0;
        int peak = 0;
        PubsubSettings settings = Settings();
        settings.MaxConcurrentMessageIdCalculations = 1;
        settings.GetMessageId = message =>
        {
            int running = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, running);
            if (!entered.IsSet)
            {
                entered.Signal();
            }

            release.Wait(Timeout);
            Interlocked.Decrement(ref active);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ILocalPeer local = Substitute.For<ILocalPeer>();
        local.ListenAddresses.Returns([TestPeers.Multiaddr(9)]);
        local.Identity.Returns(TestPeers.Identity(9));
        _ = router.StartAsync(local);
        ConnectPeers(router);

        Task[] published = [.. Enumerable.Range(0, publishers).Select(i => Task.Factory.StartNew(
            () => router.Publish(Topic, [(byte)i]),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))];

        try
        {
            Assert.That(entered.Wait(Timeout), Is.True, "No publish reached the calculation.");
            Assert.That(Volatile.Read(ref peak), Is.LessThanOrEqualTo(1),
                "Local publishes must share the same bound as received messages.");
        }
        finally
        {
            release.Set();
            Task.WaitAll(published);
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    public void ConcurrentCalculationsStayWithinTheConfiguredBound(int bound)
    {
        const int peers = 6;
        using CountdownEvent entered = new(bound);
        using ManualResetEventSlim release = new(false);
        int active = 0;
        int peak = 0;
        PubsubSettings settings = Settings();
        settings.MaxConcurrentMessageIdCalculations = bound;
        settings.GetMessageId = message =>
        {
            int running = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, running);
            if (!entered.IsSet)
            {
                entered.Signal();
            }

            release.Wait(Timeout);
            Interlocked.Decrement(ref active);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router, peers);

        Task[] rpcs = [.. Enumerable.Range(1, peers).Select(peer => Task.Factory.StartNew(
            () => router.OnRpc(TestPeers.PeerId(peer), RpcWith(NewMessage((byte)peer))),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))];

        try
        {
            Assert.That(entered.Wait(Timeout), Is.True, "The calculations never reached the bound.");
            Assert.That(Volatile.Read(ref peak), Is.LessThanOrEqualTo(bound),
                "More message id calculations ran at once than the configured bound allows.");
        }
        finally
        {
            release.Set();
            Task.WaitAll(rpcs);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    private static PubsubSettings Settings() => new()
    {
        DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        GetMessageId = message => new(message.Data.ToByteArray()),
    };

    private static PubsubRouter CreateRouter(PubsubSettings settings) => new(new PeerStore(), settings);

    private static void ConnectPeers(PubsubRouter router, int count = 2)
    {
        TaskCompletionSource closed = new();
        foreach (int index in Enumerable.Range(1, count))
        {
            Multiaddress address = TestPeers.Multiaddr(index);
            router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion, closed.Task, _ => { });
            router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics([Topic], []));
        }
    }

    private static Message NewMessage(byte data) => new() { Topic = Topic, Data = ByteString.CopyFrom([data]) };

    private static Rpc RpcWith(Message message)
    {
        Rpc rpc = new();
        rpc.Publish.Add(message);
        return rpc;
    }
}

