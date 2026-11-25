// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
/// A protocol capping its outbound streams is consulted before the session queues the request, so
/// a refused stream is never requested and the remote never negotiates it. Asserting on
/// <c>QueuedRequestCount</c> is what distinguishes this from refusing inside the protocol's own
/// <c>DialAsync</c>, which runs only once multistream has already selected it.
/// </summary>
internal class OutboundStreamLimitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ASecondOutboundStreamIsRefusedWithoutBeingQueued()
    {
        Gated protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol, TestPeers.Identity(1));
        LocalPeer.Session session = NewSession(peer, remote: 2);

        Task first = session.DialAsync(protocol);
        try
        {
            Assert.That(session.QueuedRequestCount, Is.EqualTo(1));

            // Bounded: without the gate the second dial queues and waits for an upgrade that never
            // comes, so an unbounded await would hang here rather than report the regression.
            ProtocolBusyException? busy = Assert.ThrowsAsync<ProtocolBusyException>(
                async () => await session.DialAsync(protocol).WaitAsync(Timeout));

            Assert.Multiple(() =>
            {
                Assert.That(busy!.Message, Does.Contain("already in flight"));
                Assert.That(session.QueuedRequestCount, Is.EqualTo(1),
                    "The refused dial must not reach the queue, or the stream gets negotiated.");
            });
        }
        finally
        {
            await session.DisconnectAsync();
            await first.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task TheAllowanceComesBackWhenTheDialFinishes()
    {
        Gated protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol, TestPeers.Identity(1));
        LocalPeer.Session session = NewSession(peer, remote: 2);

        Task first = session.DialAsync(protocol);
        UpgradeOptions request = session.GetRequestQueue().First();
        protocol.Release.TrySetResult();
        await peer.Upgrade(session, new Channel(), parent, request.SelectedProtocol, request, false, null);
        await first.WaitAsync(Timeout);

        // The next dial reaches the queue instead of being refused, which is what shows the
        // allowance came back. It is left pending; nothing upgrades it.
        Task second = session.DialAsync(protocol);
        try
        {
            Assert.That(session.QueuedRequestCount, Is.EqualTo(1));
        }
        finally
        {
            await session.DisconnectAsync();
            await second.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// One protocol instance is shared by every local peer of a factory, so the allowance is per
    /// local/remote pair. Two local peers dialing one server must not contend for a single one.
    /// </summary>
    [Test]
    public async Task TwoLocalPeersDialingOneRemoteEachHoldAnAllowance()
    {
        Gated protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer first = CreatePeer(parent, protocol, TestPeers.Identity(1));
        await using LocalPeer second = CreatePeer(parent, protocol, TestPeers.Identity(2));
        LocalPeer.Session fromFirst = NewSession(first, remote: 3);
        LocalPeer.Session fromSecond = NewSession(second, remote: 3);

        Task a = fromFirst.DialAsync(protocol);
        Task b = fromSecond.DialAsync(protocol);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(fromFirst.QueuedRequestCount, Is.EqualTo(1));
                Assert.That(fromSecond.QueuedRequestCount, Is.EqualTo(1), "The second local peer has its own allowance.");
            });
        }
        finally
        {
            await fromFirst.DisconnectAsync();
            await fromSecond.DisconnectAsync();
            await Task.WhenAll(a, b).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static LocalPeer.Session NewSession(LocalPeer peer, int remote)
    {
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(remote);
        return session;
    }

    private static LocalPeer CreatePeer(ProtocolRef parent, IProtocol protocol, Identity identity) => new(
        identity, null, new ProtocolStackSettings { Protocols = new() { [parent] = [new(protocol)] } });

    /// <summary>Allows one outbound stream per local/remote pair and blocks until released.</summary>
    private sealed class Gated : SymmetricSessionProtocol, ISessionProtocol, IOutboundStreamLimited
    {
        private readonly ConcurrentDictionary<(ILocalPeer, PeerId), byte> _inFlight = new();

        public string Id => "/test/outbound-limit/1.0.0";
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? TryAcquireOutboundStream(ILocalPeer local, PeerId? remote)
        {
            if (remote is null) return new Handle(null, default);
            (ILocalPeer, PeerId) key = (local, remote);
            return _inFlight.TryAdd(key, 0) ? new Handle(_inFlight, key) : null;
        }

        public string DescribeOutboundRefusal(PeerId? remote) => $"A stream to {remote} is already in flight.";

        protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
        {
            await Release.Task.WaitAsync(context.UpgradeOptions?.CancellationToken ?? default);
        }

        private sealed class Handle(ConcurrentDictionary<(ILocalPeer, PeerId), byte>? map, (ILocalPeer, PeerId) key) : IDisposable
        {
            public void Dispose() => map?.TryRemove(key, out _);
        }
    }
}
