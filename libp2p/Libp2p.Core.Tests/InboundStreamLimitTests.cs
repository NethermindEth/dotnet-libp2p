// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
public class InboundStreamLimitTests
{
    [Test]
    public void TheTotalIsCappedAcrossPeers()
    {
        InboundStreamLimit limit = new(maxConcurrent: 2);

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(TestPeers.PeerId(1)), Is.True);
            Assert.That(limit.TryAcquire(TestPeers.PeerId(2)), Is.True);
            Assert.That(limit.TryAcquire(TestPeers.PeerId(3)), Is.False, "A third peer is over the total.");
            Assert.That(limit.InFlight, Is.EqualTo(2));
        });
    }

    [Test]
    public void ReleasingLetsTheNextPeerIn()
    {
        InboundStreamLimit limit = new(maxConcurrent: 1);
        Assert.That(limit.TryAcquire(TestPeers.PeerId(1)), Is.True);
        Assert.That(limit.TryAcquire(TestPeers.PeerId(2)), Is.False);

        limit.Release(TestPeers.PeerId(1));

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(TestPeers.PeerId(2)), Is.True);
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
            Assert.That(limit.TryAcquire(greedy), Is.True);
            Assert.That(limit.TryAcquire(greedy), Is.True);
            Assert.That(limit.TryAcquire(greedy), Is.False, "The per-peer cap binds before the total.");
            Assert.That(limit.TryAcquire(TestPeers.PeerId(2)), Is.True, "Another peer still has room.");
        });
    }

    [Test]
    public void WithoutAPerPeerCapOnePeerMayUseTheTotal()
    {
        InboundStreamLimit limit = new(maxConcurrent: 3);
        PeerId peer = TestPeers.PeerId(1);

        Assert.Multiple(() =>
        {
            Assert.That(limit.TryAcquire(peer), Is.True);
            Assert.That(limit.TryAcquire(peer), Is.True);
            Assert.That(limit.TryAcquire(peer), Is.True);
            Assert.That(limit.TryAcquire(peer), Is.False);
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
            Assert.That(limit.TryAcquire(null), Is.True);
            Assert.That(limit.TryAcquire(null), Is.True);
            Assert.That(limit.TryAcquire(null), Is.False);
        });

        limit.Release(null);
        Assert.That(limit.TryAcquire(TestPeers.PeerId(1)), Is.True);
    }

    [Test]
    public void APeerIsForgottenOnceItsLastStreamEnds()
    {
        // The per-peer table must not grow with every peer ever seen.
        InboundStreamLimit limit = new(maxConcurrent: 100, maxPerPeer: 1);

        for (int i = 1; i <= 50; i++)
        {
            Assert.That(limit.TryAcquire(TestPeers.PeerId(i)), Is.True);
            limit.Release(TestPeers.PeerId(i));
        }

        Assert.Multiple(() =>
        {
            Assert.That(limit.InFlight, Is.Zero);
            Assert.That(limit.TryAcquire(TestPeers.PeerId(1)), Is.True, "A peer that finished may come back.");
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
            if (limit.TryAcquire(TestPeers.PeerId(i % 16)))
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
}
