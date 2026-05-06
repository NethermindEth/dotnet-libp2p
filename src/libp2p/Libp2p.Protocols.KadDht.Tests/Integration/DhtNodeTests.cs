// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using NUnit.Framework;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

[TestFixture]
public class DhtNodeTests
{
    private PeerId _peerId;
    private PublicKey _publicKey;
    private string[] _multiaddrs;

    [SetUp]
    public void Setup()
    {
        _peerId = new PeerId(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        _publicKey = new PublicKey(new byte[32] { 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });
        _multiaddrs = new[] { "/ip4/127.0.0.1/tcp/4001", "/ip4/192.168.1.100/tcp/4002" };
    }

    [Test]
    public void Constructor_WithRequiredParameters_ShouldCreateInstance()
    {
        // Act
        var node = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey
        };

        // Assert
        Assert.That(node.PeerId, Is.EqualTo(_peerId));
        Assert.That(node.PublicKey, Is.EqualTo(_publicKey));
        Assert.That(node.Multiaddrs, Is.Empty);
    }

    [Test]
    public void Constructor_WithAllParameters_ShouldCreateInstance()
    {
        // Act
        var node = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey,
            Multiaddrs = _multiaddrs
        };

        // Assert
        Assert.That(node.PeerId, Is.EqualTo(_peerId));
        Assert.That(node.PublicKey, Is.EqualTo(_publicKey));
        Assert.That(node.Multiaddrs.ToArray(), Is.EqualTo(_multiaddrs));
    }

    [Test]
    public void Constructor_WithConstructorParameters_ShouldCreateInstance()
    {
        // Act
        var node = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey,
            Multiaddrs = _multiaddrs
        };

        // Assert
        Assert.That(node.PeerId, Is.EqualTo(_peerId));
        Assert.That(node.PublicKey, Is.EqualTo(_publicKey));
        Assert.That(node.Multiaddrs.ToArray(), Is.EqualTo(_multiaddrs));
    }

    [Test]
    public void Equals_WithSamePeerId_ShouldReturnTrue()
    {
        // Arrange
        var node1 = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey
        };
        var node2 = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = new PublicKey(new byte[32])
        }; // Different public key

        // Act & Assert
        Assert.That(node1.Equals(node2), Is.True);
        Assert.That(node1 == node2, Is.True);
        Assert.That(node1 != node2, Is.False);
    }

    [Test]
    public void Equals_WithDifferentPeerId_ShouldReturnFalse()
    {
        // Arrange
        var node1 = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey
        };
        var node2 = new DhtNode
        {
            PeerId = new PeerId(new byte[32]),
            PublicKey = _publicKey
        };

        // Act & Assert
        Assert.That(node1.Equals(node2), Is.False);
        Assert.That(node1 == node2, Is.False);
        Assert.That(node1 != node2, Is.True);
    }

    [Test]
    public void Equals_WithNull_ShouldReturnFalse()
    {
        // Arrange
        var node = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey
        };

        // Act & Assert
        Assert.That(node.Equals(null), Is.False);
        Assert.That(node.Equals((object?)null), Is.False);
    }

    [Test]
    public void GetHashCode_WithSamePeerId_ShouldReturnSameValue()
    {
        // Arrange
        var node1 = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey
        };
        var node2 = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = new PublicKey(new byte[32])
        };

        // Act & Assert
        Assert.That(node1.GetHashCode(), Is.EqualTo(node2.GetHashCode()));
    }

    [Test]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var node = new DhtNode
        {
            PeerId = _peerId,
            PublicKey = _publicKey
        };

        // Act
        string result = node.ToString();

        // Assert
        Assert.That(result, Is.EqualTo($"DhtNode({_peerId})"));
    }
}
