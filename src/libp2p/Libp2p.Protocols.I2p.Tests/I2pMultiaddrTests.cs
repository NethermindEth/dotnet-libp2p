// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.I2p;

namespace Nethermind.Libp2p.Protocols.I2p.Tests;

public class I2pMultiaddrTests
{
    [Test]
    public void Register_AllowsGarlic32Decode()
    {
        I2pMultiaddr.Register();
        byte[] destinationBytes = I2pTestDestinations.Garlic32Bytes();
        string destination = new Garlic32(destinationBytes).Destination;

        Multiaddress address = Multiaddress.Decode($"/garlic32/{destination}");

        Assert.That(address.Has<Garlic32>(), Is.True);
        Assert.That(address.Get<Garlic32>().Destination, Is.EqualTo(destination));
        Assert.That(address.Get<Garlic32>().ToBytes(), Is.EqualTo(destinationBytes));
        Assert.That(I2pMultiaddr.GetDestination(address), Is.EqualTo($"{destination}.b32.i2p"));
    }

    [Test]
    public void FromGarlic64_AppendsPeerId()
    {
        PeerId peerId = new Identity().PeerId;
        byte[] destinationBytes = I2pTestDestinations.Garlic64Bytes();
        string destination = new Garlic64(destinationBytes).Destination;

        Multiaddress address = I2pMultiaddr.FromGarlic64(destination, peerId);

        Assert.That(address.Has<Garlic64>(), Is.True);
        Assert.That(address.Has<P2P>(), Is.True);
        Assert.That(address.Get<Garlic64>().ToBytes(), Is.EqualTo(destinationBytes));
        Assert.That(I2pMultiaddr.GetDestination(address), Is.EqualTo(destination));
        Assert.That(address.Get<P2P>().ToString(), Is.EqualTo(peerId.ToString()));
    }

    [Test]
    public void Garlic32_RejectsI2pHostnameText()
    {
        FormatException exception = Assert.Throws<FormatException>(() => new Garlic32("example.b32.i2p"))!;

        Assert.That(exception.Message, Does.Contain(".b32.i2p"));
    }
}
