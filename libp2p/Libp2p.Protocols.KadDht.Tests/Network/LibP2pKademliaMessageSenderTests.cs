// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Network;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Network;

[TestFixture]
[Obsolete("Exercises the retained legacy sender API.")]
public class LibP2pKademliaMessageSenderTests
{
    private ILocalPeer _mockLocalPeer;
    private ILoggerFactory _loggerFactory;
    private LibP2pKademliaMessageSender<PublicKey, DhtNode> _messageSender;

    [SetUp]
    public void Setup()
    {
        _mockLocalPeer = Substitute.For<ILocalPeer>();
        _mockLocalPeer.GetProtocol<PingProtocol>().Returns(new PingProtocol());
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
        Assert.Throws<ArgumentNullException>(() =>
            new LibP2pKademliaMessageSender<PublicKey, DhtNode>(null!, _loggerFactory));
    }

    [Test]
    public void Constructor_WithNullLoggerFactory_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            new LibP2pKademliaMessageSender<PublicKey, DhtNode>(_mockLocalPeer, null));
    }

    [Test]
    public async Task Ping_WithSuccessfulSession_CompletesSuccessfully()
    {
        // Arrange
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        var mockSession = Substitute.For<ISession>();
        mockSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MessageHelper.CreatePingResponse()));

        var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockSession));

        // Act
        await _messageSender.Ping(node, CancellationToken.None);

        // Assert
        await _mockLocalPeer.Received(1).DialAsync(multiaddr, Arg.Any<CancellationToken>());
        // The Kad-DHT spec forbids actively sending PING; liveness is a round trip on the ping protocol.
        await mockSession.Received(1).DialAsync<PingProtocol>(Arg.Any<CancellationToken>());
        await mockSession.DidNotReceive().DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>());
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

        var neighbourNode = CreateDhtNode("12D3KooWNeighbour", "/ip4/192.168.1.1/tcp/4001");
        var response = MessageHelper.CreateFindNodeResponse(new[] { neighbourNode });

        mockSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var multiaddr = Multiaddress.Decode(receiverNode.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockSession));

        // Act
        var result = await _messageSender.FindNeighbours(receiverNode, targetKey, CancellationToken.None);

        // Assert
        await mockSession.Received(1).DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Is<Message>(m => m.Type == Message.Types.MessageType.FindNode), Arg.Any<CancellationToken>());
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void FindNeighbours_WithFailedSession_ThrowsInvalidOperationException()
    {
        // Arrange
        var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
        var targetKey = new PublicKey(new byte[32]);
        var multiaddr = Multiaddress.Decode(receiverNode.Multiaddrs[0]);

        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ISession>(new Exception("Connection failed")));

        // Act & Assert
        Assert.That(async () => await _messageSender.FindNeighbours(receiverNode, targetKey, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void FindNeighbours_WithCancellationToken_RespectsCancellation()
    {
        // Arrange
        var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
        var targetKey = new PublicKey(new byte[32]);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.That(async () => await _messageSender.FindNeighbours(receiverNode, targetKey, cts.Token),
            Throws.TypeOf<TaskCanceledException>());
    }

    [Test]
    public async Task FindNeighbours_SendsCorrectMessageType()
    {
        // Arrange
        var receiverNode = CreateDhtNode("12D3KooWReceiver", "/ip4/127.0.0.1/tcp/4001");
        var targetKeyBytes = new byte[32];
        targetKeyBytes[0] = 42;
        var targetKey = new PublicKey(targetKeyBytes);

        var mockSession = Substitute.For<ISession>();
        Message? capturedRequest = null;

        mockSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Do<Message>(msg => capturedRequest = msg),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MessageHelper.CreateFindNodeResponse(Array.Empty<DhtNode>())));

        var multiaddr = Multiaddress.Decode(receiverNode.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockSession));

        // Act
        await _messageSender.FindNeighbours(receiverNode, targetKey, CancellationToken.None);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null, "Request should be sent");
        Assert.That(capturedRequest!.Type, Is.EqualTo(Message.Types.MessageType.FindNode), "Should send FIND_NODE message");
        Assert.That(capturedRequest.Key.IsEmpty, Is.False, "Key should be set");
        Assert.That(capturedRequest.Key.ToByteArray(), Is.EqualTo(targetKey.Bytes.ToArray()), "FIND_NODE should send raw target key bytes");
    }

    [Test]
    public async Task SessionCaching_ReusesSessionForSameNode()
    {
        // Arrange
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        var mockSession = Substitute.For<ISession>();
        mockSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MessageHelper.CreatePingResponse()));

        var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockSession));

        // Act - Ping twice
        await _messageSender.Ping(node, CancellationToken.None);
        await _messageSender.Ping(node, CancellationToken.None);

        // Assert - DialAsync should only be called once (session is cached), but each ping probes it
        await _mockLocalPeer.Received(1).DialAsync(multiaddr, Arg.Any<CancellationToken>());
        await mockSession.Received(2).DialAsync<PingProtocol>(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Ping_CachedSessionDisconnected_FailsAndNextPingRedials()
    {
        // Arrange - first ping succeeds, then the cached session loses its connection
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        var deadSession = Substitute.For<ISession>();
        var freshSession = Substitute.For<ISession>();
        var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(deadSession), Task.FromResult(freshSession));

        await _messageSender.Ping(node, CancellationToken.None);
        // A closed LocalPeer session cancels dials with its connection token, not the caller's token
        deadSession.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));

        // Act & Assert - the cached entry must not keep answering "alive"
        Assert.That(async () => await _messageSender.Ping(node, CancellationToken.None),
            Throws.InstanceOf<Libp2pException>());
        await deadSession.Received().DisconnectAsync();
        await _messageSender.Ping(node, CancellationToken.None);

        await _mockLocalPeer.Received(2).DialAsync(multiaddr, Arg.Any<CancellationToken>());
        await freshSession.Received(1).DialAsync<PingProtocol>(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Ping_CallerCancelled_KeepsCachedSession()
    {
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        var session = Substitute.For<ISession>();
        var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));
        await _messageSender.Ping(node, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();
            return Task.FromException(new OperationCanceledException(cts.Token));
        });
        Assert.That(async () => await _messageSender.Ping(node, cts.Token), Throws.InstanceOf<OperationCanceledException>());

        await session.DidNotReceive().DisconnectAsync();
    }

    [Test]
    public void Ping_WithoutPingProtocol_ThrowsSetupExceptionWithoutDialing()
    {
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        _mockLocalPeer.GetProtocol<PingProtocol>().Returns((PingProtocol?)null);

        Assert.That(async () => await _messageSender.Ping(node, CancellationToken.None),
            Throws.TypeOf<Libp2pSetupException>().With.Message.Contains(nameof(PingProtocol)));
        _mockLocalPeer.DidNotReceiveWithAnyArgs().DialAsync(default(Multiaddress)!, default);
    }

    [Test]
    public async Task FindNeighbours_InvalidOperationException_EvictsCachedSession()
    {
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        var session = Substitute.For<ISession>();
        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Message>(new InvalidOperationException("Disconnected")));
        var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));

        Assert.That(async () => await _messageSender.FindNeighbours(node, new PublicKey(new byte[32]), CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
        await _messageSender.Ping(node, CancellationToken.None);

        await _mockLocalPeer.Received(2).DialAsync(multiaddr, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SessionCaching_RemovesSessionOnFailure()
    {
        // Arrange
        var node = CreateDhtNode("12D3KooWTest1", "/ip4/127.0.0.1/tcp/4001");
        var mockSession = Substitute.For<ISession>();

        // The protocol request in the middle fails, which must drop the cached session
        mockSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Message>(new Exception("Protocol failed")));

        var multiaddr = Multiaddress.Decode(node.Multiaddrs[0]);
        _mockLocalPeer.DialAsync(multiaddr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockSession));

        // Act - First ping succeeds, the following request fails
        await _messageSender.Ping(node, CancellationToken.None);

        Assert.That(async () => await _messageSender.FindNeighbours(node, new PublicKey(new byte[32]), CancellationToken.None),
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

        mockSession1.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MessageHelper.CreatePingResponse()));
        mockSession2.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MessageHelper.CreatePingResponse()));

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

        mockSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MessageHelper.CreatePingResponse()));

        _mockLocalPeer.DialAsync(Multiaddress.Decode(node.Multiaddrs[0]), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockSession));

        await _messageSender.Ping(node, CancellationToken.None);

        // Act
        _messageSender.Dispose();

        // Assert
        await mockSession.Received(1).DisconnectAsync();
    }

    [Test]
    public void FindNeighbours_WithNoMultiaddress_ThrowsInvalidOperationException()
    {
        // Arrange
        var node = CreateDhtNode("12D3KooWTest1");
        var targetKey = new PublicKey(new byte[32]);

        // Act & Assert
        Assert.That(async () => await _messageSender.FindNeighbours(node, targetKey, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
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
            Multiaddrs = new[] { "invalid-multiaddress" }
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
    [Test]
    public async Task ClosedCachedSessionIsRedialed()
    {
        var node = new Identity().PeerId.ToDhtNode(["/ip4/127.0.0.1/tcp/4001"]);
        var first = Substitute.For<ISession>();
        var replacement = Substitute.For<ISession>();
        using var closed = new CancellationTokenSource();
        first.Closed.Returns(closed.Token);
        _mockLocalPeer.DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>()).Returns(first, replacement);
        await _messageSender.Ping(node, CancellationToken.None);
        closed.Cancel();
        await _messageSender.Ping(node, CancellationToken.None);
        await _mockLocalPeer.Received(2).DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>());
        await replacement.Received(1).DialAsync<PingProtocol>(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailureOfOldSessionDoesNotEvictConcurrentReplacement()
    {
        var semaphore = (SemaphoreSlim)typeof(LibP2pKademliaMessageSender<PublicKey, DhtNode>)
            .GetField("_connectionSemaphore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_messageSender)!;
        if (semaphore.CurrentCount < 2) semaphore.Release();
        var node = new Identity().PeerId.ToDhtNode(["/ip4/127.0.0.1/tcp/4001"]);
        var first = Substitute.For<ISession>();
        var replacement = Substitute.For<ISession>();
        using var closed = new CancellationTokenSource();
        first.Closed.Returns(closed.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        _mockLocalPeer.DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>()).Returns(first, replacement);
        Task oldRequest = _messageSender.Ping(node, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            closed.Cancel();
            await _messageSender.Ping(node, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetException(new InvalidOperationException("old request failed")); }
        Assert.ThrowsAsync<InvalidOperationException>(async () => await oldRequest);
        await _messageSender.Ping(node, CancellationToken.None);
        await _mockLocalPeer.Received(2).DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>());
        await replacement.DidNotReceive().DisconnectAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GetValueRejectsRecordForAnotherKey(bool generic)
    {
        var node = new Identity().PeerId.ToDhtNode(["/ip4/127.0.0.1/tcp/4001"]);
        var session = Substitute.For<ISession>();
        _mockLocalPeer.DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>()).Returns(session);
        _mockLocalPeer.DialAsync(node.PeerId, Arg.Any<CancellationToken>()).Returns(session);
        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(MessageHelper.CreateGetValueResponse(MessageHelper.CreatePutValueRequest([2], [3]).Record));
        if (generic)
            Assert.That((await _messageSender.GetValue(node, [1])).found, Is.False);
        else
        {
            var sender = new global::Libp2p.Protocols.KadDht.Integration.LibP2pKademliaMessageSender(_mockLocalPeer);
            Assert.That((await sender.GetValueAsync(node, [1])).HasValue, Is.False);
        }
    }

}
