// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests
{
    [TestFixture]
    public class KadDhtProtocolTests
    {
        private ILocalPeer _localPeer;
        private ILoggerFactory _loggerFactory;
        private KadDhtProtocol _protocol;
        private KadDhtOptions _options;

        [SetUp]
        public void Setup()
        {
            _localPeer = Substitute.For<ILocalPeer>();
            var identity = Substitute.For<Identity>();
            identity.PeerId.Returns(new PeerId(new byte[32]));
            _localPeer.Identity.Returns(identity);
            
            _loggerFactory = new TestContextLoggerFactory();
            
            _options = new KadDhtOptions
            {
                KSize = 20,
                Alpha = 3,
                Mode = KadDhtMode.Server
            };
            
            _protocol = new KadDhtProtocol(_localPeer, _loggerFactory, _options);
        }

        [Test]
        public void Id_ShouldReturnCorrectProtocolId()
        {
            Assert.That(_protocol.Id, Is.EqualTo("/ipfs/kad/1.0.0"));
        }

        [Test]
        public async Task PutValueAsync_ShouldStoreValueLocally()
        {
            // Arrange
            byte[] key = Encoding.UTF8.GetBytes("test-key");
            byte[] value = Encoding.UTF8.GetBytes("test-value");
            
            // Act
            bool result = await _protocol.PutValueAsync(key, value, CancellationToken.None);
            
            // Assert
            Assert.That(result, Is.True);
            
            // Verify the value was stored locally
            byte[] retrievedValue = await _protocol.GetValueAsync(key, CancellationToken.None);
            Assert.That(retrievedValue, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(retrievedValue), Is.EqualTo("test-value"));
        }

        [Test]
        public async Task ProvideAsync_ShouldAddProviderLocally()
        {
            // Arrange
            byte[] key = Encoding.UTF8.GetBytes("test-key");
            
            // Act
            bool result = await _protocol.ProvideAsync(key, CancellationToken.None);
            
            // Assert
            Assert.That(result, Is.True);
            
            // Verify the provider was added locally
            var providers = await _protocol.FindProvidersAsync(key, 1, CancellationToken.None);
            Assert.That(providers, Is.Not.Null);
            Assert.That(providers.Count(), Is.EqualTo(1));
            Assert.That(providers.First(), Is.EqualTo(_localPeer.Identity.PeerId));
        }
    }
} 