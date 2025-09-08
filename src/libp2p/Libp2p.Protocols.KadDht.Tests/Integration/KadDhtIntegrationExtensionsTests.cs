// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration
{
    [TestFixture]
    public class KadDhtIntegrationExtensionsTests
    {
        private IServiceProvider _serviceProvider;
        private ILocalPeer _localPeer;
        private IPeerFactoryBuilder _peerFactoryBuilder;

        [SetUp]
        public void Setup()
        {
            _localPeer = Substitute.For<ILocalPeer>();
            var identity = Substitute.For<Identity>();
            identity.PeerId.Returns(new PeerId(new byte[32]));
            _localPeer.Identity.Returns(identity);

            _peerFactoryBuilder = Substitute.For<IPeerFactoryBuilder>();
            _peerFactoryBuilder.AddProtocol(Arg.Any<Func<IServiceProvider, ISessionProtocol>>())
                .Returns(_peerFactoryBuilder);

            var services = new ServiceCollection();
            services.AddSingleton(_localPeer);
            services.AddLogging();
            _serviceProvider = services.BuildServiceProvider();
        }

        [Test]
        public void AddKadDht_WithMinimalParameters_ShouldNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrow(() => _peerFactoryBuilder.AddKadDht());
        }

        [Test]
        public void AddKadDht_WithConfiguration_ShouldNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrow(() => _peerFactoryBuilder.AddKadDht(options =>
            {
                options.Mode = KadDhtMode.Client;
                options.KSize = 16;
                options.Alpha = 4;
            }));
        }

        [Test]
        public void AddKadDht_WithBootstrapNodes_ShouldNotThrow()
        {
            // Arrange
            var bootstrapNodes = new[]
            {
                new DhtNode
                {
                    PeerId = new PeerId(new byte[32]),
                    PublicKey = new Kademlia.PublicKey(new byte[32])
                }
            };

            // Act & Assert
            Assert.DoesNotThrow(() => _peerFactoryBuilder.AddKadDht(bootstrapNodes: bootstrapNodes));
        }

        [Test]
        public void AddKadDht_WithNullBuilder_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                KadDhtIntegrationExtensions.AddKadDht(null!));
        }

        [Test]
        public void AddKadDht_ShouldCallAddProtocol()
        {
            // Act
            _peerFactoryBuilder.AddKadDht();

            // Assert
            _peerFactoryBuilder.Received(2).AddProtocol(Arg.Any<Func<IServiceProvider, ISessionProtocol>>());
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
            Assert.That(dhtNode.Multiaddrs, Is.EqualTo(multiaddrs));
        }

        [Test]
        public async Task RunKadDhtAsync_WithoutDhtProtocol_ShouldThrowInvalidOperationException()
        {
            // Arrange
            _localPeer.GetProtocol<KadDhtProtocol>().Returns((KadDhtProtocol?)null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _localPeer.RunKadDhtAsync(CancellationToken.None));

            Assert.That(exception.Message, Does.Contain("KadDhtProtocol not found"));
        }

        [Test]
        public async Task RunKadDhtAsync_WithDhtProtocol_ShouldCallBootstrapAndRun()
        {
            // Arrange
            var dhtProtocol = Substitute.For<KadDhtProtocol>();
            _localPeer.GetProtocol<KadDhtProtocol>().Returns(dhtProtocol);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(100)); // Cancel quickly for test

            // Act
            try
            {
                await _localPeer.RunKadDhtAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected due to cancellation
            }

            // Assert
            await dhtProtocol.Received(1).BootstrapAsync(Arg.Any<CancellationToken>());
            await dhtProtocol.Received(1).RunAsync(Arg.Any<CancellationToken>());
        }

        [Test]
        public void GetKadDht_WithDhtProtocol_ShouldReturnProtocol()
        {
            // Arrange
            var dhtProtocol = Substitute.For<KadDhtProtocol>();
            _localPeer.GetProtocol<KadDhtProtocol>().Returns(dhtProtocol);

            // Act
            var result = _localPeer.GetKadDht();

            // Assert
            Assert.That(result, Is.EqualTo(dhtProtocol));
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
}