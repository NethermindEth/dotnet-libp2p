// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// The probe reads a failed ping as evidence the peer is gone and drops the session. Two of its own
/// probes overlapping on one session is not that, and must not cost the session (#250).
/// </summary>
[TestFixture]
public class LivenessProbeContentionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public void ContentionIsRethrownWithoutDisconnectingTheSession()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ProtocolBusyException("a ping is already in flight")));

        Assert.ThrowsAsync<ProtocolBusyException>(
            async () => await LivenessProbe.ProbeAsync(session, Timeout, CancellationToken.None));

        session.DidNotReceive().DisconnectAsync();
    }

    [Test]
    public void AnyOtherFailureStillDropsTheSession()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ChannelClosedException()));

        Assert.ThrowsAsync<ChannelClosedException>(
            async () => await LivenessProbe.ProbeAsync(session, Timeout, CancellationToken.None));

        session.Received(1).DisconnectAsync();
    }
}
