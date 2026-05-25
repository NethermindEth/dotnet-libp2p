// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Protocols.NatTraversal;

namespace Libp2p.Protocols.NatTraversal.Tests;

public class HolePunchMessageCodecTests
{
    [Test]
    public void RoundTripConnectMessage()
    {
        Multiaddress[] addresses =
        [
            Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001"),
            Multiaddress.Decode("/dns4/example.com/tcp/443/wss")
        ];

        byte[] encoded = HolePunchMessageCodec.Encode(HolePunchMessage.Connect(addresses));
        HolePunchMessage decoded = HolePunchMessageCodec.Decode(encoded);

        Assert.That(decoded.Type, Is.EqualTo(HolePunchMessageType.Connect));
        Assert.That(decoded.ObservedAddresses.Select(a => a.ToString()), Is.EqualTo(addresses.Select(a => a.ToString())));
    }

    [Test]
    public void RoundTripSyncMessage()
    {
        byte[] encoded = HolePunchMessageCodec.Encode(HolePunchMessage.Sync());
        HolePunchMessage decoded = HolePunchMessageCodec.Decode(encoded);

        Assert.That(decoded.Type, Is.EqualTo(HolePunchMessageType.Sync));
        Assert.That(decoded.ObservedAddresses, Is.Empty);
    }
}
