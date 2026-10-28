// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Tests;

public class PeerResourceLimitsTests
{
    [TestCase(0)]
    [TestCase(-1)]
    public void RejectsZeroAndNegativeCounts(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerResourceLimits(maxInboundTcpConnections: count));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerResourceLimits(maxInboundTcpConnectionsPerIp: count));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerResourceLimits(maxConcurrentInitializations: count));
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(4294967295L)]
    public void RejectsInvalidDeadlines(long milliseconds)
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerResourceLimits(inboundUpgradeTimeout: timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerResourceLimits(initializationTimeout: timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerResourceLimits(negotiationTimeout: timeout));
    }

    [Test]
    public void AcceptsPositiveCountsAndFiniteDeadlineBoundaries()
    {
        TimeSpan shortest = TimeSpan.FromTicks(1);
        TimeSpan longest = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        PeerResourceLimits limits = new(1, 2, 3, shortest, longest, TimeSpan.FromSeconds(7));
        Assert.Multiple(() =>
        {
            Assert.That(limits.MaxInboundTcpConnections, Is.EqualTo(1));
            Assert.That(limits.MaxInboundTcpConnectionsPerIp, Is.EqualTo(2));
            Assert.That(limits.MaxConcurrentInitializations, Is.EqualTo(3));
            Assert.That(limits.InboundUpgradeTimeout, Is.EqualTo(shortest));
            Assert.That(limits.InitializationTimeout, Is.EqualTo(longest));
            Assert.That(limits.NegotiationTimeout, Is.EqualTo(TimeSpan.FromSeconds(7)));
        });
    }
}
