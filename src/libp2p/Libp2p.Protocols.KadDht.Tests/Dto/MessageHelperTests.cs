// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Dto;

[TestFixture]
public class MessageHelperTests
{
    [Test]
    public void FromWirePeer_AppendsPeerIdToAddressesWithoutPeerComponent()
    {
        var peerId = new Identity(new byte[32]).PeerId;
        var node = new DhtNode
        {
            PeerId = peerId,
            PublicKey = new PublicKey(peerId.Bytes),
            Multiaddrs = ["/ip4/127.0.0.1/tcp/4001"]
        };

        var wirePeer = MessageHelper.ToWirePeer(node);

        var result = MessageHelper.FromWirePeer(wirePeer);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Multiaddrs, Is.EqualTo(new[] { $"/ip4/127.0.0.1/tcp/4001/p2p/{peerId}" }));
    }

    [Test]
    public void FromWirePeer_DropsAddressesWithMismatchedPeerComponent()
    {
        var peerId = new Identity(new byte[32]).PeerId;
        var otherPeerId = new Identity(Enumerable.Repeat((byte)1, 32).ToArray()).PeerId;
        var wirePeer = new Message.Types.Peer
        {
            Id = ByteString.CopyFrom(peerId.Bytes)
        };
        wirePeer.Addrs.Add(ByteString.CopyFrom(Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{otherPeerId}").ToBytes()));

        var result = MessageHelper.FromWirePeer(wirePeer);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Multiaddrs, Is.Empty);
    }
}
