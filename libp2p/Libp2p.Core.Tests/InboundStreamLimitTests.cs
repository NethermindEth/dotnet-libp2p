// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;
using NSubstitute;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
public class InboundStreamLimitTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SynchronousListenerFailureReturnsItsAllowance(bool listenerOnly)
    {
        using MetricCapture metrics = new();
        InboundStreamLimit limit = new(2, 1);
        ISessionListenerProtocol protocol = listenerOnly ? new ThrowingListener() : new ThrowingProtocol();
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol, inboundLimit: limit);
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings { Protocols = new() { [parent] = [top] } });
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);

        for (int i = 0; i < 3; i++)
        {
            Assert.Throws<InvalidOperationException>(() => peer.Upgrade(session, new Channel(), parent, null, null, true, null));
            Assert.That(limit.InFlight, Is.Zero, "A synchronous throw must return the acquired allowance.");
        }
        Assert.That(metrics.Measurements.Where(m => m.Name == "libp2p.streams.inbound.active").Select(m => m.Value),
            Is.EqualTo(new long[] { 1, -1, 1, -1, 1, -1 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RefusalResetsBeforeDispatchAndReportsTheBindingScope(bool perPeer)
    {
        using MetricCapture metrics = new();
        InboundStreamLimit limit = new(perPeer ? 8 : 1, perPeer ? 1 : 0);
        PeerId countedPeer = TestPeers.PeerId(perPeer ? 2 : 3);
        Assert.That(limit.TryAcquire(countedPeer, out _), Is.True);
        DeferredProtocol protocol = new(Task.CompletedTask);
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol, inboundLimit: limit);
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings { Protocols = new() { [parent] = [top] } });
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);
        IChannel channel = Substitute.For<IChannel>();
        try
        {
            await peer.Upgrade(session, channel, parent, null, null, true, null);

            Assert.That(protocol.Calls, Is.Zero);
            await channel.Received(1).AbortAsync();
            await channel.DidNotReceive().CloseAsync();
            var refusal = metrics.Measurements.Where(m => m.Name == "libp2p.streams.inbound.refused").ToArray();
            Assert.That(refusal, Has.Length.EqualTo(1));
            Assert.That(refusal[0].Value, Is.EqualTo(1));
            Assert.That(refusal[0].Scope, Is.EqualTo(perPeer ? "peer" : "total"));
            Assert.That(refusal[0].Protocol, Is.EqualTo(protocol.Id));
        }
        finally
        {
            limit.Release(countedPeer);
        }
    }

    [TestCase(TaskStatus.RanToCompletion)]
    [TestCase(TaskStatus.Faulted)]
    [TestCase(TaskStatus.Canceled)]
    public async Task ActiveMetricBalancesWhenAHandlerFinishes(TaskStatus outcome)
    {
        using MetricCapture metrics = new();
        InboundStreamLimit limit = new(1);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DeferredProtocol protocol = new(completion.Task);
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol, inboundLimit: limit);
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings { Protocols = new() { [parent] = [top] } });
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);
        Task upgrade = peer.Upgrade(session, new Channel(), parent, null, null, true, null);
        try
        {
            Assert.That(metrics.Measurements.Where(m => m.Name == "libp2p.streams.inbound.active").Sum(m => m.Value), Is.EqualTo(1));
            if (outcome == TaskStatus.Faulted)
            {
                completion.SetException(new InvalidOperationException("handler failure"));
                Assert.ThrowsAsync<InvalidOperationException>(async () => await upgrade);
            }
            else if (outcome == TaskStatus.Canceled)
            {
                completion.SetCanceled();
                Assert.ThrowsAsync<TaskCanceledException>(async () => await upgrade);
            }
            else
            {
                completion.SetResult();
                await upgrade;
            }
            await metrics.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(limit.InFlight, Is.Zero);
            Assert.That(metrics.Measurements.Where(m => m.Name == "libp2p.streams.inbound.active").Select(m => (m.Value, m.Protocol)),
                Is.EqualTo(new[] { (1L, (string?)protocol.Id), (-1L, (string?)protocol.Id) }));
        }
        finally
        {
            completion.TrySetCanceled();
            await upgrade.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public void TheTotalIsCappedAcrossPeers()
    {
        InboundStreamLimit limit = new(maxConcurrent: 2);

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(TestPeers.PeerId(1), out _), Is.True);
            Assert.That(limit.TryAcquire(TestPeers.PeerId(2), out _), Is.True);
            Assert.That(limit.TryAcquire(TestPeers.PeerId(3), out _), Is.False, "A third peer is over the total.");
            Assert.That(limit.InFlight, Is.EqualTo(2));
        });
    }

    [Test]
    public void ReleasingLetsTheNextPeerIn()
    {
        InboundStreamLimit limit = new(maxConcurrent: 1);
        Assert.That(limit.TryAcquire(TestPeers.PeerId(1), out _), Is.True);
        Assert.That(limit.TryAcquire(TestPeers.PeerId(2), out _), Is.False);

        limit.Release(TestPeers.PeerId(1));

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(TestPeers.PeerId(2), out _), Is.True);
            Assert.That(limit.InFlight, Is.EqualTo(1));
        });
    }

    [Test]
    public void OnePeerCannotTakeTheWholeAllowance()
    {
        InboundStreamLimit limit = new(maxConcurrent: 8, maxPerPeer: 2);
        PeerId greedy = TestPeers.PeerId(1);

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(greedy, out _), Is.True);
            Assert.That(limit.TryAcquire(greedy, out _), Is.True);
            Assert.That(limit.TryAcquire(greedy, out _), Is.False, "The per-peer cap binds before the total.");
            Assert.That(limit.TryAcquire(TestPeers.PeerId(2), out _), Is.True, "Another peer still has room.");
        });
    }

    [Test]
    public void WithoutAPerPeerCapOnePeerMayUseTheTotal()
    {
        InboundStreamLimit limit = new(maxConcurrent: 3);
        PeerId peer = TestPeers.PeerId(1);

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(peer, out _), Is.True);
            Assert.That(limit.TryAcquire(peer, out _), Is.True);
            Assert.That(limit.TryAcquire(peer, out _), Is.True);
            Assert.That(limit.TryAcquire(peer, out _), Is.False);
        });
    }

    [Test]
    public void AnUnidentifiedStreamCountsAgainstTheTotalOnly()
    {
        // Before identity is known there is nobody to attribute the stream to, so the per-peer cap
        // cannot apply; the total still must.
        InboundStreamLimit limit = new(maxConcurrent: 2, maxPerPeer: 1);

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(null, out _), Is.True);
            Assert.That(limit.TryAcquire(null, out _), Is.True);
            Assert.That(limit.TryAcquire(null, out _), Is.False);
        });

        limit.Release(null);
        Assert.That(limit.TryAcquire(TestPeers.PeerId(1), out _), Is.True);
    }

    [Test]
    public void APeerIsForgottenOnceItsLastStreamEnds()
    {
        // The per-peer table must not grow with every peer ever seen.
        InboundStreamLimit limit = new(maxConcurrent: 100, maxPerPeer: 1);

        for (int i = 1; i <= 50; i++)
        {
            Assert.That(limit.TryAcquire(TestPeers.PeerId(i), out _), Is.True);
            limit.Release(TestPeers.PeerId(i));
        }

        Assert.Multiple(() =>
        {
            Assert.That(limit.InFlight, Is.Zero);
            var peers = (Dictionary<PeerId, int>)typeof(InboundStreamLimit).GetField("_perPeer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(limit)!;
            Assert.That(peers, Is.Empty, "Finished peers must not leave retained dictionary entries.");
            Assert.That(limit.TryAcquire(TestPeers.PeerId(1), out _), Is.True, "A peer that finished may come back.");
        });
    }

    [Test]
    public void ConcurrentAcquisitionNeverExceedsTheTotal()
    {
        const int cap = 4;
        InboundStreamLimit limit = new(maxConcurrent: cap);
        int granted = 0;

        Parallel.For(0, 256, i =>
        {
            if (limit.TryAcquire(TestPeers.PeerId(i % 16), out _))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(granted, Is.EqualTo(cap), "Exactly the allowance should have been handed out.");
            Assert.That(limit.InFlight, Is.EqualTo(cap));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ATotalThatAllowsNothingIsRejected(int maxConcurrent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new InboundStreamLimit(maxConcurrent));

    [Test]
    public void ANegativePerPeerCapIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new InboundStreamLimit(8, maxPerPeer: -1));

    private class ThrowingListener : ISessionListenerProtocol
    {
        public string Id => "/test/throwing-listener";
        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new InvalidOperationException("synchronous listener failure");
    }

    private sealed class ThrowingProtocol : ThrowingListener, ISessionProtocol
    {
        public Task DialAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
    }

    private sealed class DeferredProtocol(Task completion) : ISessionProtocol
    {
        public string Id => "/test/deferred-listener";
        public int Calls;
        public Task DialAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
        public Task ListenAsync(IChannel channel, ISessionContext context)
        {
            Calls++;
            return completion;
        }
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<(string Name, long Value, string? Protocol, string? Scope)> Measurements { get; } = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "dotnet-libp2p" && instrument.Name.StartsWith("libp2p.streams.inbound.")) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? protocol = null;
                string? scope = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "protocol") protocol = tag.Value as string;
                    if (tag.Key == "scope") scope = tag.Value as string;
                }
                Measurements.Enqueue((instrument.Name, value, protocol, scope));
                if (value < 0) Released.TrySetResult();
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
