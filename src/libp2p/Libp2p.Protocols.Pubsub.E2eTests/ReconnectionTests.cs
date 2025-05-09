// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Pubsub.E2eTests;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

public class ReconnectionTests
{

#if DEBUG
    [Test]
    public async Task Test_CanReconnect_AsListener()
    {
        string commonTopic = "test";

        int totalCount = 2;
        await using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);
        foreach ((_, PeerStore peerStore) in test.PeerStores.Skip(1))
        {
            peerStore.Discover([.. test.Peers[0].ListenAddresses]);
        }

        await test.WaitForFullMeshAsync(commonTopic);

        IpTcpProtocol.TriggerDisconnection(test.Peers[0].Identity.PeerId);

        await test.WaitForBrokenMeshAsync(commonTopic);
        TestContext.Out.WriteLine("Disconnected");

        await test.WaitForFullMeshAsync(commonTopic);
    }

    [Test]
    public async Task Test_CanReconnect_AsDialer()
    {
        string commonTopic = "test";

        int totalCount = 2;
        await using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);
        foreach ((_, PeerStore peerStore) in test.PeerStores.Skip(1))
        {
            peerStore.Discover([.. test.Peers[0].ListenAddresses]);
        }

        await test.WaitForFullMeshAsync(commonTopic);

        IpTcpProtocol.TriggerDisconnection(test.Peers[1].Identity.PeerId);

        await test.WaitForBrokenMeshAsync(commonTopic);
        TestContext.Out.WriteLine("Disconnected");

        await test.WaitForFullMeshAsync(commonTopic);
    }
#endif

    [Test]
    public async Task Test_CanTrace()
    {
        string commonTopic = "test";

        int totalCount = 2;
        await using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);
        foreach ((_, PeerStore peerStore) in test.PeerStores.Skip(1))
        {
            peerStore.Discover([.. test.Peers[0].ListenAddresses]);
        }

        await test.WaitForFullMeshAsync(commonTopic);
    }
}
