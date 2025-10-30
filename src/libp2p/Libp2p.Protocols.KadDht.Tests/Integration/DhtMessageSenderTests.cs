// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration
{
    [TestFixture]
    public class DhtMessageSenderTests
    {
        private ILocalPeer _localPeer;
        private ILoggerFactory _loggerFactory;
        private DhtMessageSender _messageSender;
        private DhtNode _targetNode;

        [SetUp]
        public void Setup()
        {
            _localPeer = Substitute.For<ILocalPeer>();
            _loggerFactory = new TestContextLoggerFactory();
            _messageSender = new DhtMessageSender(_localPeer, _loggerFactory);

            var peerId = new PeerId(new byte[32]);
            var publicKey = new PublicKey(new byte[32]);
            _targetNode = new DhtNode
            {
                PeerId = peerId,
                PublicKey = publicKey
            };
        }

        [TearDown]
        public async Task TearDown()
        {
            await _localPeer.DisposeAsync();
            (_loggerFactory as IDisposable)?.Dispose();
        }

        [Test]
        public void Constructor_WithValidParameters_ShouldCreateInstance()
        {
            // Act & Assert
            Assert.DoesNotThrow(() => new DhtMessageSender(_localPeer, _loggerFactory));
        }

        [Test]
        public void Constructor_WithNullLocalPeer_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new DhtMessageSender(null!, _loggerFactory));
        }

        [Test]
        public void Ping_WithNullReceiver_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(() =>
                _messageSender.Ping(null!, CancellationToken.None));
        }

        [Test]
        public void FindNeighbours_WithNullReceiver_ShouldThrowArgumentNullException()
        {

            var target = new PublicKey(new byte[32]);

            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(() =>
                _messageSender.FindNeighbours(null!, target, CancellationToken.None));
        }

        [Test]
        public void FindNeighbours_WithNullTarget_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(() =>
                _messageSender.FindNeighbours(_targetNode, null!, CancellationToken.None));
        }

        [Test]
        public void Ping_WithCancellationToken_ShouldRespectCancellation()
        {

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            _localPeer.DialAsync(Arg.Any<PeerId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromCanceled<ISession>(cts.Token));

            // Act & Assert
            Assert.ThrowsAsync<TaskCanceledException>(() =>
                _messageSender.Ping(_targetNode, cts.Token));
        }

        [Test]
        public void FindNeighbours_WithCancellationToken_ShouldRespectCancellation()
        {

            var target = new PublicKey(new byte[32]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            _localPeer.DialAsync(Arg.Any<PeerId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromCanceled<ISession>(cts.Token));

            // Act & Assert
            Assert.ThrowsAsync<TaskCanceledException>(() =>
                _messageSender.FindNeighbours(_targetNode, target, cts.Token));
        }
    }
}
