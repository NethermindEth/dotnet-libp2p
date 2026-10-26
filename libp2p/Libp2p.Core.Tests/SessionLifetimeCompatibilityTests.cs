// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Multiformats.Address;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

internal class SessionLifetimeCompatibilityTests
{
    [TestCase(nameof(ISession.RemotePeerId))]
    [TestCase(nameof(ISession.Closed))]
    public void SessionMetadataHasADefaultImplementation(string propertyName)
    {
        var getter = typeof(ISession).GetProperty(propertyName)!.GetMethod!;

        Assert.That(getter.IsAbstract, Is.False, "Existing session implementations must not require a new member.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LegacySessionDerivesTheDestinationPeerIdentity(bool relayed)
    {
        Multiaddress address = relayed
            ? $"/p2p/{TestPeers.PeerId(3)}/p2p-circuit/p2p/{TestPeers.PeerId(2)}"
            : TestPeers.Multiaddr(2);
        ISession session = new LegacySession(address);

        Assert.That(session.RemotePeerId, Is.EqualTo(TestPeers.PeerId(2)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LegacySessionWithoutADestinationPeerIdentityReportsTheMissingIdentity(bool relayed)
    {
        Multiaddress address = relayed
            ? $"/p2p/{TestPeers.PeerId(3)}/p2p-circuit"
            : "/ip4/127.0.0.1/tcp/1234";
        ISession session = new LegacySession(address);

        Assert.Throws<InvalidOperationException>(() => _ = session.RemotePeerId);
    }

    [Test]
    public async Task LegacySessionDoesNotAdvertiseUnsupportedLifetimeNotifications()
    {
        ISession session = new TestRemotePeer(TestPeers.Multiaddr(2));

        Assert.That(session.RemotePeerId, Is.EqualTo(TestPeers.PeerId(2)));
        Assert.That(session.Closed, Is.EqualTo(CancellationToken.None));
        Assert.That(session.Closed.CanBeCanceled, Is.False);
        await session.DisconnectAsync();
        Assert.That(session.Closed, Is.EqualTo(CancellationToken.None));
        Assert.That(session.Closed.IsCancellationRequested, Is.False);
    }

    private sealed class LegacySession(Multiaddress address) : ISession
    {
        public Multiaddress RemoteAddress => address;
        public Activity? Activity => null;
        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol => Task.CompletedTask;
        public Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default)
            where TProtocol : ISessionProtocol<TRequest, TResponse> => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
    }
}
