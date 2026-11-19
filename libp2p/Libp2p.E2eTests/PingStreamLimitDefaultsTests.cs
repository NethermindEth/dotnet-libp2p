// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p;
using NUnit.Framework;

namespace Libp2p.E2eTests;

/// <summary>
/// The inbound cap mechanism is <c>InboundStreamLimit</c> and is covered by its own tests; what is
/// specific to ping is the number. "The listening peer SHOULD accept at most two streams per peer"
/// — https://github.com/libp2p/specs/blob/master/ping/ping.md (#250).
/// </summary>
[TestFixture]
public class PingStreamLimitDefaultsTests
{
    [Test]
    public void PingAllowsTheSpecifiedTwoInboundStreamsPerPeer()
    {
        BuiltInProtocolLimits limits = new();

        Assert.Multiple(() =>
        {
            Assert.That(limits.Ping, Is.Not.Null, "ping has to carry a limit for the cap to apply at all");
            Assert.That(limits.Ping!.MaxPerPeer, Is.EqualTo(2));
            Assert.That(limits.Ping.MaxConcurrent, Is.GreaterThan(limits.Ping.MaxPerPeer),
                "the total still has to bound breadth across peers");
        });
    }
}
