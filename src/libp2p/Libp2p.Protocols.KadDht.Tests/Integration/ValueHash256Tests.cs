// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Kademlia;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

[TestFixture]
public class ValueHash256Tests
{
    [Test]
    public void Bytes_PreservesMutableArrayAndSpanContract()
    {
        byte[] bytes = new byte[ValueHash256.HashLength];
        var hash = ValueHash256.Zero;

        hash.Bytes = bytes;
        hash.BytesAsSpan[0] = 7;

        Assert.That(hash.Bytes, Is.SameAs(bytes));
        Assert.That(bytes[0], Is.EqualTo(7));
        Assert.That(typeof(ValueHash256).GetProperty(nameof(ValueHash256.Bytes))!.PropertyType,
            Is.EqualTo(typeof(byte[])));
    }

    [TestCase(1)]
    [TestCase(255)]
    [TestCase(256)]
    public void GetRandomHashAtDistance_ReturnsHashAtRequestedDistance(int distance)
    {
        var current = ValueHash256.FromBytes(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var random = new Random(17);

        var result = ValueHash256.GetRandomHashAtDistance(current, distance, random);

        Assert.That(ValueHash256.CalculateLogDistance(current, result), Is.EqualTo(distance));
    }

    [Test]
    public void FromBytes_ClonesInput()
    {
        byte[] bytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var hash = ValueHash256.FromBytes(bytes);

        bytes[0] ^= 0xFF;

        Assert.That(hash.Bytes[0], Is.EqualTo(0));
    }
}
