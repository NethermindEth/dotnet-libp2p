// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Pubsub.E2eTests;
using Nethermind.Libp2p.Protocols.Pubsub;


int totalCount = 9;
PubsubTestSetup test = new();

await test.StartPeersAsync(totalCount);
test.StartPubsub();
test.Subscribe("test");

foreach ((int index, PubsubRouter router) in test.Routers.Skip(1))
{
    test.PeerStores[index].Discover(test.Peers[0].ListenAddresses.ToArray());
}

await test.WaitForFullMeshAsync("test");

test.PrintState(true);

