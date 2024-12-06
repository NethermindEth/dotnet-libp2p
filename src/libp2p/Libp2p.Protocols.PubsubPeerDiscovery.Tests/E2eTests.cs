// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Pubsub.E2eTests;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework.Internal;

namespace Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Tests;

public class E2eTests
{
    [Test]
    public async Task Test_NetworkEstablished()
    {
        int totalCount = 10;
        PubsubTestSetup test = new();

        await test.StartPeersAsync(totalCount);
        test.StartPubsub();
        test.AddPubsubPeerDiscovery();
        test.Subscribe("test");

        foreach ((int index, PubsubRouter router) in test.Routers.Skip(1))
        {
            test.PeerStores[index].Discover(test.Peers[0].ListenAddresses.ToArray());
        }


        await test.WaitForFullMeshAsync("test", 150_000);

        test.PrintState();
    }
}
