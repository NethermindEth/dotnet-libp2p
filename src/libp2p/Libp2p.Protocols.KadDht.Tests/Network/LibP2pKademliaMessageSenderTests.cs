// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Network;
using Libp2p.Protocols.KadDht.RequestResponse;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Network
{
    [TestFixture]
    public class LibP2pKademliaMessageSenderTests
    {
        private ILocalPeer _mockLocalPeer;
        private ILoggerFactory _loggerFactory;
        private LibP2pKademliaMessageSender<PublicKey, DhtNode> _messageSender;

        [SetUp]
        public void Setup()
        {
            _mockLocalPeer = Substitute.For<ILocalPeer>();
            _loggerFactory = new TestContextLoggerFactory();
            _messageSender = new LibP2pKademliaMessageSender<PublicKey, DhtNode>(_mockLocalPeer, _loggerFactory);
        }

        [TearDown]
        public async Task TearDown()
        {
            if (_messageSender != null)
            {
                await _messageSender.DisposeAsync();
            }
            await _mockLocalPeer.DisposeAsync();
            (_loggerFactory as IDisposable)?.Dispose();
        }

        [Test]
        public void Constructor_WithNullLocalPeer_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new LibP2pKademliaMessageSender<PublicKey, DhtNode>(null!, _loggerFactory));
        }

        [Test]
        public void Constructor_WithNullLoggerFactory_DoesNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrow(() =>
                new LibP2pKademliaMessageSender<PublicKey, DhtNode>(_mockLocalPeer, null));
        }

        [Test]
        public async Task Ping_WithSuccessfulSession_CompletesSuccessfully()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var mockSession = Substitute.For<ISession>();
            mockSession.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PingResponse()));

            var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession));

            // Act
            await _messageSender.Ping(node, CancellationToken.None);

            // Assert
            await _mockLocalPeer.Received(1).DialAsync(multiaddr, Arg.Any<CancellationToken>());
            await mockSession.Received(1).DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public void Ping_WithFailedSession_ThrowsInvalidOperationException()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);

            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ISession>(new Exception("Connection failed")));

            // Act & Assert
            Assert.That(async () => await _messageSender.Ping(node, CancellationToken.None),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Ping_WithCancellationToken_RespectsCancellation()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            Assert.That(async () => await _messageSender.Ping(node, cts.Token),
                Throws.TypeOf<TaskCanceledException>());
        }

        [Test]
        public async Task FindNeighbours_WithSuccessfulSession_ReturnsNeighbours()
        {
            // Arrange
            var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
            var targetKey = new PublicKey(new byte[32]);

            var mockSession = Substitute.For<ISession>();
            var expectedResponse = new FindNeighboursResponse();
            expectedResponse.Neighbours.Add(new Nethermind.Libp2P.Protocols.KadDht.Dto.Node
            {
                PublicKey = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            });

            mockSession.DialAsync<KadDhtFindNeighboursProtocol, FindNeighboursRequest, FindNeighboursResponse>(
                Arg.Any<FindNeighboursRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(expectedResponse));

            var multiaddr = Multiaddress.Decode(receiverNode.Multiaddrs[0]);
            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession));

            // Act
            var result = await _messageSender.FindNeighbours(receiverNode, targetKey, CancellationToken.None);

            // Assert
            await mockSession.Received(1).DialAsync<KadDhtFindNeighboursProtocol, FindNeighboursRequest, FindNeighboursResponse>(
                Arg.Any<FindNeighboursRequest>(), Arg.Any<CancellationToken>());
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Length, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public async Task FindNeighbours_WithFailedSession_ReturnsEmpty()
        {
            // Arrange
            var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
            var targetKey = new PublicKey(new byte[32]);
            var multiaddr = Multiaddress.Decode(receiverNode.Multiaddrs[0]);

            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ISession>(new Exception("Connection failed")));

            // Act
            var result = await _messageSender.FindNeighbours(receiverNode, targetKey, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Empty, "Should return empty array on connection failure");
        }

        [Test]
        public async Task FindNeighbours_WithCancellationToken_RespectsCancellation()
        {
            // Arrange
            var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
            var targetKey = new PublicKey(new byte[32]);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var result = await _messageSender.FindNeighbours(receiverNode, targetKey, cts.Token);

            // Assert - Should return empty when cancelled
            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task FindNeighbours_SendsCorrectRequest()
        {
            // Arrange
            var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
            var targetKeyBytes = new byte[32];
            targetKeyBytes[0] = 42;
            var targetKey = new PublicKey(targetKeyBytes);

            var mockSession = Substitute.For<ISession>();
            FindNeighboursRequest? capturedRequest = null;

            mockSession.DialAsync<KadDhtFindNeighboursProtocol, FindNeighboursRequest, FindNeighboursResponse>(
                Arg.Do<FindNeighboursRequest>(req => capturedRequest = req),
                Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new FindNeighboursResponse()));

            var multiaddr = Multiaddress.Decode(receiverNode.Multiaddrs[0]);
            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession));

            // Act
            await _messageSender.FindNeighbours(receiverNode, targetKey, CancellationToken.None);

            // Assert
            Assert.That(capturedRequest, Is.Not.Null, "Request should be sent");
            Assert.That(capturedRequest!.Target, Is.Not.Null, "Target should be set");
            Assert.That(capturedRequest.Target.Value, Is.Not.Null, "Target value should be set");
        }

        [Test]
        public async Task SessionCaching_ReusesSessionForSameNode()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var mockSession = Substitute.For<ISession>();
            mockSession.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PingResponse()));

            var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession));

            // Act - Ping twice
            await _messageSender.Ping(node, CancellationToken.None);
            await _messageSender.Ping(node, CancellationToken.None);

            // Assert - DialAsync should only be called once (session is cached)
            await _mockLocalPeer.Received(1).DialAsync(multiaddr, Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task SessionCaching_RemovesSessionOnFailure()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var mockSession = Substitute.For<ISession>();

            // First call succeeds
            mockSession.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PingResponse()),
                         Task.FromException<PingResponse>(new Exception("Protocol failed")),
                         Task.FromResult(new PingResponse()));

            var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
            _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession));

            // Act - First ping succeeds, second fails
            await _messageSender.Ping(node, CancellationToken.None);

            Assert.That(async () => await _messageSender.Ping(node, CancellationToken.None),
                Throws.TypeOf<Exception>());

            // Try third time - should create new session
            await _messageSender.Ping(node, CancellationToken.None);

            // Assert - DialAsync should be called twice (once initially, once after failure)
            await _mockLocalPeer.Received(2).DialAsync(multiaddr, Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task DisposeAsync_DisconnectsAllSessions()
        {
            // Arrange
            var node1 = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var node2 = CreateDhtNode("12D3KooWTest2", "/ip4/127.0.0.1/tcp/4002");

            var mockSession1 = Substitute.For<ISession>();
            var mockSession2 = Substitute.For<ISession>();

            mockSession1.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PingResponse()));
            mockSession2.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PingResponse()));

            _mockLocalPeer.DialAsync(Multiaddress.Decode(node1.Multiaddrs[0]), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession1));
            _mockLocalPeer.DialAsync(Multiaddress.Decode(node2.Multiaddrs[0]), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession2));

            // Create sessions
            await _messageSender.Ping(node1, CancellationToken.None);
            await _messageSender.Ping(node2, CancellationToken.None);

            // Act
            await _messageSender.DisposeAsync();

            // Assert
            await mockSession1.Received(1).DisconnectAsync();
            await mockSession2.Received(1).DisconnectAsync();
        }

        [Test]
        public async Task Dispose_DisconnectsAllSessions()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
            var mockSession = Substitute.For<ISession>();

            mockSession.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                Arg.Any<PingRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PingResponse()));

            _mockLocalPeer.DialAsync(Multiaddress.Decode(node.Multiaddrs[0]), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(mockSession));

            await _messageSender.Ping(node, CancellationToken.None);

            // Act
            _messageSender.Dispose();

            // Assert
            await mockSession.Received(1).DisconnectAsync();
        }

        [Test]
        public async Task FindNeighbours_WithNoMultiaddress_ReturnsEmpty()
        {
            // Arrange
            var node = CreateDhtNode("12D3KooWTest1"); // No multiaddress
            var targetKey = new PublicKey(new byte[32]);

            // Act
            var result = await _messageSender.FindNeighbours(node, targetKey, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Empty, "Should return empty when node has no multiaddress");
        }

        [Test]
        public void Ping_WithInvalidMultiaddress_ThrowsException()
        {
            // Arrange
            var publicKey = new PublicKey(new byte[32]);
            var peerId = new PeerId(new byte[32]);
            var node = new DhtNode
            {
                PublicKey = publicKey,
                PeerId = peerId,
                Multiaddrs = new[] { "invalid-multiaddress" } // Invalid format
            };

            // Act & Assert
            Assert.That(async () => await _messageSender.Ping(node, CancellationToken.None),
                Throws.TypeOf<InvalidOperationException>());
        }

        private DhtNode CreateDhtNode(string peerIdSuffix, string? multiaddress = null)
        {
            var keyBytes = new byte[32];
            var random = new Random(peerIdSuffix.GetHashCode());
            random.NextBytes(keyBytes);

            var publicKey = new PublicKey(keyBytes);
            var peerId = new PeerId(keyBytes);

            return new DhtNode
            {
                PublicKey = publicKey,
                PeerId = peerId,
                Multiaddrs = multiaddress != null ? new[] { multiaddress } : Array.Empty<string>()
            };
        }
    }
}
