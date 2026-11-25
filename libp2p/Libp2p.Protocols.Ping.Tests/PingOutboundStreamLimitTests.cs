// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Ping.Tests;

/// <summary>
/// "The dialing peer MUST NOT keep more than one outbound stream for the ping protocol per peer"
/// — https://github.com/libp2p/specs/blob/master/ping/ping.md (#250).
/// <para>
/// Ping supplies the allowance through <see cref="IOutboundStreamLimited"/>; the session takes it
/// before queueing the dial, so the refused stream is never opened. These cases cover the
/// allowance itself. That it is taken ahead of negotiation is covered by
/// <c>OutboundStreamLimitTests</c> against the real session dial path.
/// </para>
/// </summary>
[TestFixture]
[CancelAfter(30_000)]
public class PingOutboundStreamLimitTests
{
    // Two distinct, valid peer ids. This project does not reference Core.TestsBase, so they are
    // spelled out rather than taken from TestPeers.
    private static readonly PeerId RemoteOne = new("12D3KooWAfPDpPRRRBrmqy9is2zjU5srQ4hKuZitiGmh4NTTpS2d");
    private static readonly PeerId RemoteTwo = new("12D3KooWJwYWjPLsTKiZ7eMjDagCZh9Fqt1UERLKoPb5QQNByrAF");

    private static ILocalPeer LocalPeer() => Substitute.For<ILocalPeer>();

    [Test]
    public void ASecondOutboundPingToOnePeerIsRefused()
    {
        PingProtocol protocol = new();
        ILocalPeer local = LocalPeer();
        PeerId remote = RemoteOne;

        using IDisposable? first = protocol.TryAcquireOutboundStream(local, remote);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(protocol.TryAcquireOutboundStream(local, remote), Is.Null);
            Assert.That(protocol.DescribeOutboundRefusal(remote), Does.Contain("already in flight"));
        });
    }

    [Test]
    public void TheNextPingSucceedsOnceTheFirstReleases()
    {
        PingProtocol protocol = new();
        ILocalPeer local = LocalPeer();
        PeerId remote = RemoteOne;

        protocol.TryAcquireOutboundStream(local, remote)!.Dispose();

        Assert.That(protocol.TryAcquireOutboundStream(local, remote), Is.Not.Null);
    }

    [Test]
    public void OnePeerInFlightDoesNotBlockAnother()
    {
        PingProtocol protocol = new();
        ILocalPeer local = LocalPeer();

        using IDisposable? first = protocol.TryAcquireOutboundStream(local, RemoteOne);

        Assert.That(protocol.TryAcquireOutboundStream(local, RemoteTwo), Is.Not.Null);
    }

    /// <summary>
    /// One protocol instance is shared by every local peer built from the same factory, so the
    /// allowance is keyed on the pair. Without that, two local peers dialing one server would
    /// contend for a single ping.
    /// </summary>
    [Test]
    public void TwoLocalPeersDialingOneRemoteDoNotContend()
    {
        PingProtocol protocol = new();
        PeerId remote = RemoteOne;

        using IDisposable? first = protocol.TryAcquireOutboundStream(LocalPeer(), remote);

        Assert.That(protocol.TryAcquireOutboundStream(LocalPeer(), remote), Is.Not.Null);
    }

    /// <summary>Disposing twice must not hand a second caller someone else's allowance.</summary>
    [Test]
    public void ReleasingTwiceDoesNotFreeTheNextHolder()
    {
        PingProtocol protocol = new();
        ILocalPeer local = LocalPeer();
        PeerId remote = RemoteOne;

        IDisposable held = protocol.TryAcquireOutboundStream(local, remote)!;
        held.Dispose();
        IDisposable next = protocol.TryAcquireOutboundStream(local, remote)!;
        held.Dispose();

        Assert.That(protocol.TryAcquireOutboundStream(local, remote), Is.Null,
            "The stale handle must not release the allowance the next caller holds.");
        next.Dispose();
    }

    /// <summary>There is no peer to count against until the handshake names one.</summary>
    [Test]
    public void AnUnidentifiedRemoteIsNotGated()
    {
        PingProtocol protocol = new();
        ILocalPeer local = LocalPeer();

        using IDisposable? first = protocol.TryAcquireOutboundStream(local, null);

        Assert.That(protocol.TryAcquireOutboundStream(local, null), Is.Not.Null);
    }
}
