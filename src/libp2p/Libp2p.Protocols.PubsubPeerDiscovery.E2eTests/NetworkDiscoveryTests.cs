// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
using NUnit.Framework;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

public class NetworkDiscoveryTests
{
    [Test]
    public async Task Test_NetworkDiscoveredByEveryPeer()
    {
        string commonTopic = "test";

        int totalCount = 5;
        await using PubsubDiscoveryE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);
        foreach ((_, PeerStore peerStore) in test.PeerStores.Skip(1))
        {
            peerStore.Discover(test.Peers[0].ListenAddresses.ToArray());
        }

        // Use 30 second timeout for CI environment compatibility
        await test.WaitForFullMeshAsync(commonTopic, timeoutMs: 30_000);
    }
}
