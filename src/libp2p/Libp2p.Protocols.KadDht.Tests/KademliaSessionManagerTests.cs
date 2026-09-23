// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;
using SessionNode = Libp2p.Protocols.KadDht.TestNode;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests;

[TestFixture]
public class KademliaSessionManagerTests
{
    [Test]
    public async Task DiscoverDoesNotReturnTheLocalNode()
    {
        PeerId localPeerId = new Identity(new byte[32]).PeerId;
        var sender = Substitute.For<global::Libp2p.Protocols.KadDht.IKademliaMessageSender<PublicKey, SessionNode>>();
        KademliaSessionManager manager = new(new SessionOptions(), localPeerId, sender);

        SessionNode[] nodes = await manager.DiscoverAsync<SessionNode>(
            new PublicKey(localPeerId.Bytes.ToArray()), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(nodes, Does.Not.Contain(new SessionNode(localPeerId)));
        await manager.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task UsesHostIdentityAndStopsBeforeDisposal()
    {
        PeerId localPeerId = new Identity(new byte[32]).PeerId;
        var sender = Substitute.For<global::Libp2p.Protocols.KadDht.IKademliaMessageSender<PublicKey, SessionNode>>();
        KademliaSessionManager manager = new(new SessionOptions(), localPeerId, sender);
        var config = (Nethermind.Kademlia.KademliaConfig<SessionNode>)typeof(KademliaSessionManager)
            .GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        Assert.That(config.CurrentNodeId.Id, Is.EqualTo(localPeerId));

        Task run = manager.RunAsync(CancellationToken.None);
        await manager.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(run.IsCompletedSuccessfully, Is.True);
        Assert.Throws<ObjectDisposedException>(() => manager.RunAsync(CancellationToken.None));
    }
}
