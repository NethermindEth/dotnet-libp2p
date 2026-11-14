// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

public class MultiaddressExtensionsTests
{
    [Test]
    public void GetPeerId_UsesDestinationAfterCircuit()
    {
        PeerId relay = TestPeers.PeerId(1);
        PeerId destination = TestPeers.PeerId(2);

        Multiaddress direct = $"/ip4/127.0.0.1/tcp/4001/p2p/{destination}";
        Multiaddress relayOnly = $"/ip4/127.0.0.1/tcp/4001/p2p/{relay}/p2p-circuit";
        Multiaddress relayed = $"{relayOnly}/p2p/{destination}";

        Assert.Multiple(() =>
        {
            Assert.That(direct.GetPeerId(), Is.EqualTo(destination));
            Assert.That(relayOnly.GetPeerId(), Is.Null);
            Assert.That(relayed.GetPeerId(), Is.EqualTo(destination));
            Assert.That(((Multiaddress?)null).GetPeerId(), Is.Null);
        });
    }

    [TestCase(KeyType.Ed25519)]
    [TestCase(KeyType.Rsa)]
    public void GetPeerId_ReturnsMultihashBytesWithoutSharingThem(KeyType keyType)
    {
        PeerId expected = new Identity(keyType: keyType).PeerId;
        Multiaddress address = $"/ip4/127.0.0.1/tcp/4001/p2p/{expected}";

        PeerId first = address.GetPeerId()!;
        first.Bytes[^1] ^= 0xFF;
        PeerId second = address.GetPeerId()!;

        Assert.Multiple(() =>
        {
            Assert.That(second.Bytes, Is.EqualTo(expected.Bytes));
            Assert.That(second.ToString(), Is.EqualTo(expected.ToString()));
            Assert.That(address.ToString(), Does.EndWith($"/p2p/{expected}"));
        });
    }
}
