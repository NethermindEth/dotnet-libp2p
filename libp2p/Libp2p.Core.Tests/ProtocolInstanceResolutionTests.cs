// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class ProtocolInstanceResolutionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitRegisteredInstanceKeepsItsParentAllowance(bool registerLeaf)
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeProtocol protocol = new(release.Task);
        ProtocolRef parent = new(protocol);
        InboundStreamLimit limit = new(1);
        ProtocolRef child = new(protocol, inboundLimit: limit);
        ProtocolRef otherRegistration = new(protocol);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [child], [otherRegistration] = [] } };
        if (registerLeaf) settings.Protocols[child] = [];
        await using LocalPeer peer = new(TestPeers.Identity(1), null, settings);
        LocalPeer.Session session = new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(2) } };
        Task call = peer.Upgrade(session, new Channel(), parent, protocol, null, true, null);
        try
        {
            Assert.That(limit.InFlight, Is.EqualTo(1));
            await peer.Upgrade(session, new Channel(), parent, protocol, null, true, null);
            Assert.That(protocol.Calls, Is.EqualTo(1), "The explicit instance must use the current parent's capped registration.");
        }
        finally { release.TrySetResult(); await call; }
        Assert.That(limit.InFlight, Is.Zero);
    }

    [Test]
    public async Task EqualProtocolObjectsStillResolveByInstanceIdentity()
    {
        ProbeProtocol registered = new(Task.CompletedTask);
        ProbeProtocol supplied = new(Task.CompletedTask);
        ProtocolRef parent = new(registered);
        ProtocolRef child = new(registered);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [child], [child] = [] } };
        await using LocalPeer peer = new(TestPeers.Identity(1), null, settings);
        LocalPeer.Session session = new(peer);

        await peer.Upgrade(session, new Channel(), parent, supplied, null, false, null);

        Assert.That(registered.Calls, Is.Zero);
        Assert.That(supplied.Calls, Is.EqualTo(1));
        Assert.That(supplied.Children, Is.Empty);
        Assert.That(settings.Protocols, Has.Count.EqualTo(2));
        Assert.Throws<Libp2pSetupException>(() => peer.GetProtocolsFor(new ProtocolRef(supplied, false)).ToArray());
    }

    private sealed class ProbeProtocol(Task completion) : ISessionProtocol
    {
        public string Id => "/same-id";
        public int Calls;
        public IProtocol[]? Children;
        public Task DialAsync(IChannel channel, ISessionContext context)
        {
            Calls++;
            Children = context.SubProtocols.ToArray();
            return completion;
        }
        public Task ListenAsync(IChannel channel, ISessionContext context)
        {
            Calls++;
            return completion;
        }
        public override bool Equals(object? obj) => obj is ProbeProtocol;
        public override int GetHashCode() => 0;
    }
}
