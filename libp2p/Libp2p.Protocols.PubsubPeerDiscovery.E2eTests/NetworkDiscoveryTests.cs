// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
using NUnit.Framework;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

public class NetworkDiscoveryTests
{
    [Test]
    [Retry(3)]
    public async Task Test_NetworkDiscoveredByEveryPeer()
    {
        string commonTopic = "test";

        int totalCount = 3;
        await using PubsubDiscoveryE2eTestSetup test = new();
        // This test only checks discovery; its idle topics should not penalize missing message deliveries.
        test.DefaultSettings.TopicScoreParams[commonTopic] = new() { MeshMessageDeliveriesThreshold = 0 };
        foreach (string discoveryTopic in test.DefaultDiscoverySettings.Topics)
            test.DefaultSettings.TopicScoreParams[discoveryTopic] = new() { MeshMessageDeliveriesThreshold = 0 };

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);

        // Each peer discovers peer 0; wait for an actual connection before proceeding.
        foreach ((int index, PeerStore peerStore) in test.PeerStores.Skip(1))
        {
            TaskCompletionSource connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            test.Peers[index].OnConnected += _ => connectedTcs.TrySetResult();

            peerStore.Discover(test.Peers[0].ListenAddresses.ToArray());

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            try { await connectedTcs.Task.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { /* proceed — mesh will catch up */ }
        }

        await test.WaitForFullMeshAsync(commonTopic);
    }

    [Test]
    public async Task Test_ConnectedPeerIsDiscoveredImmediatelyOnStartup()
    {
        await using PubsubDiscoveryE2eTestSetup test = new();
        // Far longer than the wait below: only the broadcast sent on startup can make the peer discoverable in time.
        test.DefaultDiscoverySettings = new PubsubPeerDiscoverySettings { Interval = 60_000 };
        foreach (string discoveryTopic in test.DefaultDiscoverySettings.Topics)
            test.DefaultSettings.TopicScoreParams[discoveryTopic] = new() { MeshMessageDeliveriesThreshold = 0 };

        // AddPeersAsync starts discovery before the peers are connected, so restart it once they are.
        await test.AddPeersAsync(2);
        foreach (PubsubPeerDiscoveryProtocol discovery in test.Discovery.Values)
            await discovery.DisposeAsync();

        _ = await test.Peers[1].DialAsync([.. test.Peers[0].ListenAddresses]);

        // Observe the announcements peer 1 sends to peer 0 on the discovery topic.
        TaskCompletionSource announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ITopic topic = test.Routers[0].GetTopic(test.DefaultDiscoverySettings.Topics[0]);
        topic.OnMessage += (_, _) => announced.TrySetResult();

        for (int i = 0; i < 2; i++)
            test.Discovery[i] = new PubsubPeerDiscoveryProtocol(test.Routers[i], test.PeerStores[i], test.DefaultDiscoverySettings, test.Peers[i]);

        await test.Discovery[0].StartDiscoveryAsync(test.Peers[0].ListenAddresses);
        await Task.Delay(1000); // let peer 1 learn that peer 0 is subscribed
        await test.Discovery[1].StartDiscoveryAsync(test.Peers[1].ListenAddresses);

        await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The announcement must have populated peer 0's PeerStore with peer 1, and vice versa.
        Assert.That(test.PeerStores[0].TryGetPeerInfo(test.Peers[1].Identity.PeerId, out _), Is.True, "peer 0 did not discover peer 1");
        Assert.That(test.PeerStores[1].TryGetPeerInfo(test.Peers[0].Identity.PeerId, out _), Is.True, "peer 1 did not discover peer 0");
    }
}
