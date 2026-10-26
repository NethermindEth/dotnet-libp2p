// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectRateLimiterTests
{
    [Test]
    public void RejectsPacketOverMaxSize()
    {
        long now = 1_000;
        WebRtcDirectRateLimiter limiter = new(maxPacketBytes: 128, nowMs: () => now);

        bool accepted = limiter.TryAccept(new IPEndPoint(IPAddress.Loopback, 4001), 129, out string? reason);

        Assert.That(accepted, Is.False);
        Assert.That(reason, Is.EqualTo("packet-too-large"));
    }

    [Test]
    public void PerEndpointBurst_IsEnforced()
    {
        long now = 1_000;
        WebRtcDirectRateLimiter limiter = new(
            endpointBurst: 2,
            endpointRatePerSecond: 0,
            globalMessageBurst: 100,
            globalMessageRatePerSecond: 0,
            globalByteBurst: 1_000_000,
            globalByteRatePerSecond: 0,
            nowMs: () => now);

        IPEndPoint endpoint = new(IPAddress.Parse("192.0.2.10"), 5000);

        Assert.That(limiter.TryAccept(endpoint, 50, out _), Is.True);
        Assert.That(limiter.TryAccept(endpoint, 50, out _), Is.True);
        Assert.That(limiter.TryAccept(endpoint, 50, out string? reason), Is.False);
        Assert.That(reason, Is.EqualTo("endpoint-message-rate"));
    }

    [Test]
    public void GlobalMessageRate_IsEnforcedAcrossEndpoints()
    {
        long now = 1_000;
        WebRtcDirectRateLimiter limiter = new(
            endpointBurst: 10,
            endpointRatePerSecond: 0,
            globalMessageBurst: 2,
            globalMessageRatePerSecond: 0,
            globalByteBurst: 1_000_000,
            globalByteRatePerSecond: 0,
            nowMs: () => now);

        Assert.That(limiter.TryAccept(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 4000), 10, out _), Is.True);
        Assert.That(limiter.TryAccept(new IPEndPoint(IPAddress.Parse("192.0.2.2"), 4001), 10, out _), Is.True);
        Assert.That(limiter.TryAccept(new IPEndPoint(IPAddress.Parse("192.0.2.3"), 4002), 10, out string? reason), Is.False);
        Assert.That(reason, Is.EqualTo("global-message-rate"));
    }

    [Test]
    public void Refill_AllowsTrafficAfterWait()
    {
        long now = 1_000;
        WebRtcDirectRateLimiter limiter = new(
            endpointBurst: 1,
            endpointRatePerSecond: 1,
            globalMessageBurst: 100,
            globalMessageRatePerSecond: 100,
            globalByteBurst: 1_000_000,
            globalByteRatePerSecond: 1_000_000,
            nowMs: () => now);

        IPEndPoint endpoint = new(IPAddress.Parse("198.51.100.10"), 5000);
        Assert.That(limiter.TryAccept(endpoint, 10, out _), Is.True);
        Assert.That(limiter.TryAccept(endpoint, 10, out _), Is.False);

        now += 1_100;

        Assert.That(limiter.TryAccept(endpoint, 10, out _), Is.True);
    }
}
