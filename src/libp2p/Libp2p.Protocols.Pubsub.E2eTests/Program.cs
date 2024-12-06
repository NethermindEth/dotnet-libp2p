// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Pubsub.E2eTests;
using Nethermind.Libp2p.Core;


int totalCount = 2;
PubsubTestSetup test = new();

await test.StartPeersAsync(totalCount);
//test.StartPubsub();
//test.Subscribe("test");

//foreach ((int index, PubsubRouter router) in test.Routers.Skip(1))
//{
//    test.PeerStores[index].Discover(test.Peers[0].ListenAddresses.ToArray());
//}

//await test.WaitForFullMeshAsync("test");

//test.PrintState(true);


ISession session = await test.Peers[0].DialAsync(test.Peers[1].ListenAddresses.ToArray());
Console.WriteLine(await session.DialAsync<TestRequestResponseProtocol, int, int>(1));
