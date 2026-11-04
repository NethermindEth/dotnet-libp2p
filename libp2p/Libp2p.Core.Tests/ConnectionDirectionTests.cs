// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class ConnectionDirectionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task DirectionSurvivesStreamRoleChangesAndSessionReuse(bool listener)
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        DirectionProtocol protocol = new();
        ProtocolRef root = new(protocol), app = new(protocol);
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings
        {
            TopProtocols = [root],
            Protocols = new() { [root] = [app], [app] = [] },
        });
        LocalPeer.Session session = new(peer);
        using INewConnectionContext connection = peer.CreateConnection(root, session, listener, null);
        connection.State.RemoteAddress = TestPeers.Multiaddr(2);
        using INewSessionContext context = connection.UpgradeToSession();
        await session.Connected.WaitAsync(token);

        ISession reused = await peer.DialAsync(TestPeers.Multiaddr(2), token);
        Assert.That(reused, Is.SameAs(session), "DialAsync must retain an established session, including an inbound one.");
        // Consume the real session dial request as a multiplexer does, then open a remote stream.
        Task dial = reused.DialAsync(protocol, token);
        UpgradeOptions request = context.DialRequests.First();
        await context.Upgrade(new Channel(), protocol, request with { ModeOverride = UpgradeModeOverride.Dial });
        await dial.WaitAsync(token);
        await context.Upgrade(new Channel(), protocol, new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
        ConnectionDirection expected = listener ? ConnectionDirection.Inbound : ConnectionDirection.Outbound;
        Assert.That(protocol.Seen, Is.EqualTo(new[] { (false, expected), (true, expected) }));
    }

    [Test]
    public void LegacyAndSubstituteContextsDefaultToUnknown()
    {
        IConnectionContext legacy = new LegacyContext();
        Assert.Multiple(() =>
        {
            Assert.That(legacy.Direction, Is.EqualTo(ConnectionDirection.Unknown));
            Assert.That(Substitute.For<ISessionContext>().Direction, Is.EqualTo(ConnectionDirection.Unknown));
            Assert.That(typeof(IConnectionContext).GetProperty(nameof(IConnectionContext.Direction))!.GetMethod!.IsAbstract, Is.False);
        });
    }

    private sealed class DirectionProtocol : ISessionProtocol
    {
        public string Id => "/test/connection-direction/1.0.0";
        public List<(bool Listener, ConnectionDirection Direction)> Seen { get; } = [];

        public Task DialAsync(IChannel channel, ISessionContext context)
        {
            Seen.Add((false, context.Direction));
            return Task.CompletedTask;
        }

        public Task ListenAsync(IChannel channel, ISessionContext context)
        {
            Seen.Add((true, context.Direction));
            return Task.CompletedTask;
        }
    }

    // Deliberately implements the old interface without a Direction member.
    private sealed class LegacyContext : IConnectionContext
    {
        public ILocalPeer Peer => throw new NotSupportedException();
        public Activity? Activity => null;
        public string Id => "legacy";
        public State State { get; } = new();
        public UpgradeOptions? UpgradeOptions => null;
        public IEnumerable<IProtocol> SubProtocols => [];
        public INewConnectionContext CreateConnection() => throw new NotSupportedException();
        public INewSessionContext UpgradeToSession() => throw new NotSupportedException();
        public void ListenerReady(Multiaddress addr) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public IChannel Upgrade(UpgradeOptions? options = null) => throw new NotSupportedException();
        public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel parentChannel, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? options = null) => throw new NotSupportedException();
    }
}
