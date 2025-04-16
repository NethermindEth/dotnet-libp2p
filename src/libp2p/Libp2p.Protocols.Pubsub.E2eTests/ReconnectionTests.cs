// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Pubsub.E2eTests;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

public class ReconnectionTests
{
    [Test]
    public async Task Test_CanReconnect()
    {
        string commonTopic = "test";

        int totalCount = 2;
        using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);
        foreach ((_, PeerStore peerStore) in test.PeerStores.Skip(1))
        {
            peerStore.Discover(test.Peers[0].ListenAddresses.ToArray());
        }


        await test.WaitForFullMeshAsync(commonTopic);

        IpTcpProtocol.TriggerDisconnection.Invoke(test.Peers[0].Identity.PeerId);
        TestContext.Out.WriteLine("Disconnected");


        await Task.Delay(100_000);
        await test.WaitForFullMeshAsync(commonTopic, 1000000000);
    }
}
