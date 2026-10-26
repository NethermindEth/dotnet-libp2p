// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
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
}
