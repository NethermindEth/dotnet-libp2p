// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

[TestFixture]
public class KadDhtIntegrationExtensionsTests
{
    private ILocalPeer _localPeer;

    [SetUp]
    public void Setup()
    {
        _localPeer = Substitute.For<ILocalPeer>();
        _localPeer.Identity.Returns(new Identity(new byte[32]));
        _localPeer.ListenAddresses.Returns(new System.Collections.ObjectModel.ObservableCollection<Multiformats.Address.Multiaddress>());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _localPeer.DisposeAsync();
    }

    [Test]
    public void AddKadDht_WithNullBuilder_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            KadDhtIntegrationExtensions.AddKadDht(null!));
    }

    [Test]
    public void ToDhtNode_WithPeerId_ShouldCreateDhtNode()
    {
        // Arrange
        var peerId = new PeerId(new byte[32]);

        // Act
        var dhtNode = peerId.ToDhtNode();

        // Assert
        Assert.That(dhtNode.PeerId, Is.EqualTo(peerId));
        Assert.That(dhtNode.PublicKey, Is.Not.Null);
        Assert.That(dhtNode.Multiaddrs, Is.Empty);
    }

    [Test]
    public void ToDhtNode_WithMultiaddrs_ShouldCreateDhtNodeWithAddresses()
    {
        // Arrange
        var peerId = new PeerId(new byte[32]);
        var multiaddrs = new[] { "/ip4/127.0.0.1/tcp/4001", "/ip4/192.168.1.1/tcp/4002" };

        // Act
        var dhtNode = peerId.ToDhtNode(multiaddrs);

        // Assert
        Assert.That(dhtNode.PeerId, Is.EqualTo(peerId));
        Assert.That(dhtNode.PublicKey, Is.Not.Null);
        Assert.That(dhtNode.Multiaddrs.ToArray(), Is.EqualTo(multiaddrs));
    }

    [Test]
    public void GetKadDht_WithDhtProtocol_ShouldReturnProtocol()
    {
        // Arrange
        var loggerFactory = new TestContextLoggerFactory();
        var options = new KadDhtOptions();
        var valueStore = new InMemoryValueStore(options.MaxStoredValues, loggerFactory);
        var providerStore = new InMemoryProviderStore(options.MaxProvidersPerKey, loggerFactory);
        var messageSender = Substitute.For<global::Libp2p.Protocols.KadDht.Kademlia.IKademliaMessageSender<global::Libp2p.Protocols.KadDht.Kademlia.PublicKey, DhtNode>>();
        var dhtProtocol = new KadDhtProtocol(_localPeer, messageSender, options, valueStore, providerStore, loggerFactory);
        _localPeer.GetProtocol<KadDhtProtocol>().Returns(dhtProtocol);

        // Act
        var result = _localPeer.GetKadDht();

        // Assert
        Assert.That(result, Is.EqualTo(dhtProtocol));
        loggerFactory.Dispose();
    }

    [Test]
    public void GetKadDht_WithoutDhtProtocol_ShouldReturnNull()
    {
        // Arrange
        _localPeer.GetProtocol<KadDhtProtocol>().Returns((KadDhtProtocol?)null);

        // Act
        var result = _localPeer.GetKadDht();

        // Assert
        Assert.That(result, Is.Null);
    }
}
