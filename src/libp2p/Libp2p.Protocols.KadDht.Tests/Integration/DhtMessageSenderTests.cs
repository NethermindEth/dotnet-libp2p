// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration
{
    [TestFixture]
    public class DhtMessageSenderTests
    {
        private ILocalPeer _localPeer;
        private ILogger<DhtMessageSender> _logger;
        private DhtMessageSender _messageSender;
        private DhtNode _targetNode;

        [SetUp]
        public void Setup()
        {
            _localPeer = Substitute.For<ILocalPeer>();
            _logger = Substitute.For<ILogger<DhtMessageSender>>();
            _messageSender = new DhtMessageSender(_localPeer, _logger);

            var peerId = new PeerId(new byte[32]);
            var publicKey = new PublicKey(new byte[32]);
            _targetNode = new DhtNode
            {
                PeerId = peerId,
                PublicKey = publicKey
            };
        }

        [Test]
        public void Constructor_WithValidParameters_ShouldCreateInstance()
        {
            // Act & Assert
            Assert.DoesNotThrow(() => new DhtMessageSender(_localPeer, _logger));
        }

        [Test]
        public void Constructor_WithNullLocalPeer_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new DhtMessageSender(null!, _logger));
        }

        [Test]
        public async Task PingAsync_ShouldReturnFalseWhenNotImplemented()
        {
            // Act
            bool result = await _messageSender.PingAsync(_targetNode, CancellationToken.None);

            // Assert
            Assert.That(result, Is.False);
        }

        [Test]
        public async Task FindNeighboursAsync_ShouldReturnEmptyArrayWhenNotImplemented()
        {
            // Arrange
            var targetKey = new PublicKey(new byte[32]);

            // Act
            var result = await _messageSender.FindNeighboursAsync(_targetNode, targetKey, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task GetValueAsync_ShouldReturnNullWhenNotImplemented()
        {
            // Arrange
            var key = new byte[32];

            // Act
            byte[]? result = await _messageSender.GetValueAsync(_targetNode, key, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task PutValueAsync_ShouldReturnFalseWhenNotImplemented()
        {
            // Arrange
            var key = new byte[32];
            var value = new byte[64];

            // Act
            bool result = await _messageSender.PutValueAsync(_targetNode, key, value, CancellationToken.None);

            // Assert
            Assert.That(result, Is.False);
        }

        [Test]
        public async Task FindProvidersAsync_ShouldReturnEmptyArrayWhenNotImplemented()
        {
            // Arrange
            var key = new byte[32];

            // Act
            var result = await _messageSender.FindProvidersAsync(_targetNode, key, 10, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task AddProviderAsync_ShouldReturnFalseWhenNotImplemented()
        {
            // Arrange
            var key = new byte[32];
            var provider = new DhtNode
            {
                PeerId = new PeerId(new byte[32]),
                PublicKey = new PublicKey(new byte[32])
            };

            // Act
            bool result = await _messageSender.AddProviderAsync(_targetNode, key, provider, CancellationToken.None);

            // Assert
            Assert.That(result, Is.False);
        }

        [Test]
        public async Task PingAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            var result = await _messageSender.PingAsync(_targetNode, cts.Token);
            Assert.That(result, Is.False); // Should complete quickly and return false
        }

        [Test]
        public async Task FindNeighboursAsync_WithNullTarget_ShouldReturnEmptyArray()
        {
            // Arrange
            var targetKey = new PublicKey(new byte[32]);

            // Act
            var result = await _messageSender.FindNeighboursAsync(null!, targetKey, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task GetValueAsync_WithNullTarget_ShouldReturnNull()
        {
            // Arrange
            var key = new byte[32];

            // Act
            byte[]? result = await _messageSender.GetValueAsync(null!, key, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task PutValueAsync_WithNullTarget_ShouldReturnFalse()
        {
            // Arrange
            var key = new byte[32];
            var value = new byte[64];

            // Act
            bool result = await _messageSender.PutValueAsync(null!, key, value, CancellationToken.None);

            // Assert
            Assert.That(result, Is.False);
        }
    }
}