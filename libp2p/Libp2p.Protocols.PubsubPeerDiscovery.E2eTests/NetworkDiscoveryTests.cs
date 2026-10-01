// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
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
}
