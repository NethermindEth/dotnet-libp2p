// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Network;
using Nethermind.Kademlia;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests;

[TestFixture]
public class DhtClientTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ValueOperations_LookUpTheHashOfTheRawKey(bool put)
    {
        const string key = "routing-target";
        var routingTable = Substitute.For<IRoutingTable<DhtNode, ValueHash256>>();
        routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), true).Returns(Array.Empty<DhtNode>());
        SharedDhtState state = new(routingTable);
        await using LibP2pKademliaMessageSender<PublicKey, DhtNode> sender =
            new(Substitute.For<ILocalPeer>());
        DhtClient client = new(state, sender);

        if (put)
            await client.PutValueAsync(key, "value");
        else
            await client.GetValueAsync(key);

        ValueHash256 expected = PublicKey.ComputeHash(Encoding.UTF8.GetBytes(key));
        routingTable.Received(1).GetKNearestNeighbour(expected, true);
    }
}
