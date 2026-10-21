// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class ConnectionManagementTests
{
    [Test]
    public async Task Application_can_open_gossip_on_an_existing_session_with_automatic_connections_disabled()
    {
        const string topic = "application-connections";
        await using PubsubE2eTestSetup test = new();
        test.DefaultSettings.AutoConnect = false;
        await test.AddPeersAsync(2);
        test.Subscribe(topic);
        test.PeerStores[0].Discover([.. test.Peers[1].ListenAddresses]);

        ISession session = await test.Peers[0].DialAsync(test.Peers[1].Identity.PeerId)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(test.PeerStores[0].GetPeerInfo(session.RemotePeerId).SignedPeerRecord, Is.Not.Null,
            "Identify still stores verified metadata before the application opens gossip");
        Assert.That(((IRoutingStateContainer)test.Routers[0]).ConnectedPeers, Is.Empty);
        Assert.That(((IRoutingStateContainer)test.Routers[1]).ConnectedPeers, Is.Empty);

        TaskCompletionSource<byte[]> receivedByFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<byte[]> receivedBySecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Routers[0].GetTopic(topic).OnMessage += (_, data) => receivedByFirst.TrySetResult(data);
        test.Routers[1].GetTopic(topic).OnMessage += (_, data) => receivedBySecond.TrySetResult(data);
        Task gossip = session.DialAsync<GossipsubProtocolV13>();
        try
        {
            await test.WaitForFullMeshAsync(topic, 10_000);
            test.Routers[0].Publish(topic, [1, 2, 3]);
            test.Routers[1].Publish(topic, [4, 5, 6]);

            Assert.That(await receivedByFirst.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(await receivedBySecond.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
        finally
        {
            await test.Routers[0].DisposeAsync();
            await gossip.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
