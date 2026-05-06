// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Multiformats.Address;
using NSubstitute;
using NUnit.Framework;
using KademliaPublicKey = global::Libp2p.Protocols.KadDht.Kademlia.PublicKey;
using KademliaMessageSender = global::Libp2p.Protocols.KadDht.Kademlia.IKademliaMessageSender<global::Libp2p.Protocols.KadDht.Kademlia.PublicKey, global::Libp2p.Protocols.KadDht.Integration.DhtNode>;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests;

[TestFixture]
public class KadDhtProtocolTests
{
    private ILocalPeer _localPeer;
    private ILoggerFactory _loggerFactory;
    private KadDhtProtocol _protocol;
    private KadDhtOptions _options;
    private IValueStore _valueStore;
    private IProviderStore _providerStore;
    private KademliaMessageSender _messageSender;
    private IDhtMessageSender _dhtMessageSender;

    [SetUp]
    public void Setup()
    {
        _localPeer = Substitute.For<ILocalPeer>();
        _localPeer.Identity.Returns(new Identity(new byte[32]));
        _localPeer.ListenAddresses.Returns(new System.Collections.ObjectModel.ObservableCollection<Multiformats.Address.Multiaddress>());

        _loggerFactory = new TestContextLoggerFactory();

        _options = new KadDhtOptions
        {
            KSize = 20,
            Alpha = 10,
            Mode = KadDhtMode.Server,
            MaxStoredValues = 100,
            MaxProvidersPerKey = 20
        };

        _valueStore = new InMemoryValueStore(_options.MaxStoredValues, _loggerFactory);
        _providerStore = new InMemoryProviderStore(_options.MaxProvidersPerKey, _loggerFactory);
        _messageSender = Substitute.For<KademliaMessageSender>();
        _dhtMessageSender = Substitute.For<IDhtMessageSender>();

        _protocol = new KadDhtProtocol(_localPeer, _messageSender, _dhtMessageSender, _options, _valueStore, _providerStore, _loggerFactory);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _localPeer.DisposeAsync();
        (_loggerFactory as IDisposable)?.Dispose();
    }

    [Test]
    public void Constructor_WithNullLocalPeer_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KadDhtProtocol(null!, _messageSender, _dhtMessageSender, _options, _valueStore, _providerStore, _loggerFactory));
    }

    [Test]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KadDhtProtocol(_localPeer, _messageSender, _dhtMessageSender, null!, _valueStore, _providerStore, _loggerFactory));
    }

    [Test]
    public void Constructor_WithNullValueStore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KadDhtProtocol(_localPeer, _messageSender, _dhtMessageSender, _options, null!, _providerStore, _loggerFactory));
    }

    [Test]
    public void Constructor_WithNullProviderStore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KadDhtProtocol(_localPeer, _messageSender, _dhtMessageSender, _options, _valueStore, null!, _loggerFactory));
    }

    [Test]
    public void Id_ShouldReturnCorrectProtocolId()
    {
        Assert.That(_protocol.Id, Is.EqualTo("/ipfs/kad/1.0.0"));
    }

    [Test]
    public async Task PutValueAsync_WithValidKeyAndValue_ReturnsTrue()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        byte[] value = Encoding.UTF8.GetBytes("test-value");

        // Act
        bool result = await _protocol.PutValueAsync(key, value, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task PutValueAsync_StoresValueLocally()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        byte[] value = Encoding.UTF8.GetBytes("test-value");

        // Act
        await _protocol.PutValueAsync(key, value, CancellationToken.None);

        // Verify the value was stored locally
        byte[]? retrievedValue = await _protocol.GetValueAsync(key, CancellationToken.None);

        // Assert
        Assert.That(retrievedValue, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(retrievedValue!), Is.EqualTo("test-value"));
    }

    [Test]
    public void PutValueAsync_WithNullKey_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _protocol.PutValueAsync(null!, new byte[] { 1, 2, 3 }, CancellationToken.None));
    }

    [Test]
    public void PutValueAsync_WithNullValue_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _protocol.PutValueAsync(new byte[] { 1, 2, 3 }, null!, CancellationToken.None));
    }

    [Test]
    public void PutValueAsync_WithEmptyKey_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.PutValueAsync(Array.Empty<byte>(), new byte[] { 1, 2, 3 }, CancellationToken.None));
    }

    [Test]
    public void PutValueAsync_WithEmptyValue_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.PutValueAsync(new byte[] { 1, 2, 3 }, Array.Empty<byte>(), CancellationToken.None));
    }

    [Test]
    public void PutValueAsync_WithValueExceedingMaxSize_ThrowsArgumentException()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        byte[] largeValue = new byte[_options.MaxValueSize + 1];

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.PutValueAsync(key, largeValue, CancellationToken.None));
    }

    [Test]
    public async Task GetValueAsync_WithExistingKey_ReturnsValue()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        byte[] value = Encoding.UTF8.GetBytes("test-value");
        await _protocol.PutValueAsync(key, value, CancellationToken.None);

        // Act
        byte[]? retrievedValue = await _protocol.GetValueAsync(key, CancellationToken.None);

        // Assert
        Assert.That(retrievedValue, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(retrievedValue!), Is.EqualTo("test-value"));
    }

    [Test]
    public async Task GetValueAsync_WithNonExistentKey_ReturnsNull()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("non-existent-key");

        // Act
        byte[]? retrievedValue = await _protocol.GetValueAsync(key, CancellationToken.None);

        // Assert
        Assert.That(retrievedValue, Is.Null);
    }

    [Test]
    public void GetValueAsync_WithNullKey_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _protocol.GetValueAsync(null!, CancellationToken.None));
    }

    [Test]
    public void GetValueAsync_WithEmptyKey_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.GetValueAsync(Array.Empty<byte>(), CancellationToken.None));
    }

    [Test]
    public async Task ProvideAsync_InServerMode_ReturnsTrue()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");

        // Act
        bool result = await _protocol.ProvideAsync(key, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task ProvideAsync_AddsProviderLocally()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");

        // Act
        await _protocol.ProvideAsync(key, CancellationToken.None);

        // Verify the provider was added locally
        var providers = await _protocol.FindProvidersAsync(key, 1, CancellationToken.None);

        // Assert
        Assert.That(providers, Is.Not.Null);
        Assert.That(providers.Count(), Is.EqualTo(1));
        Assert.That(providers.First(), Is.EqualTo(_localPeer.Identity.PeerId));
    }

    [Test]
    public void ProvideAsync_WithNullKey_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _protocol.ProvideAsync(null!, CancellationToken.None));
    }

    [Test]
    public void ProvideAsync_WithEmptyKey_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.ProvideAsync(Array.Empty<byte>(), CancellationToken.None));
    }

    [Test]
    public async Task ProvideAsync_InClientMode_ReturnsFalse()
    {
        // Arrange
        _options.Mode = KadDhtMode.Client;
        var clientProtocol = new KadDhtProtocol(_localPeer, _messageSender, _dhtMessageSender, _options, _valueStore, _providerStore, _loggerFactory);
        byte[] key = Encoding.UTF8.GetBytes("test-key");

        // Act
        bool result = await clientProtocol.ProvideAsync(key, CancellationToken.None);

        // Assert
        Assert.That(result, Is.False, "Client mode should not accept provider records");
    }

    [Test]
    public async Task FindProvidersAsync_WithExistingProviders_ReturnsProviders()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        await _protocol.ProvideAsync(key, CancellationToken.None);

        // Act
        var providers = await _protocol.FindProvidersAsync(key, 10, CancellationToken.None);

        // Assert
        Assert.That(providers, Is.Not.Null);
        Assert.That(providers.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task FindProvidersAsync_WithNonExistentKey_ReturnsEmpty()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("non-existent-key");

        // Act
        var providers = await _protocol.FindProvidersAsync(key, 10, CancellationToken.None);

        // Assert
        Assert.That(providers, Is.Empty);
    }

    [Test]
    public void FindProvidersAsync_WithNullKey_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _protocol.FindProvidersAsync(null!, 10, CancellationToken.None));
    }

    [Test]
    public void FindProvidersAsync_WithEmptyKey_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.FindProvidersAsync(Array.Empty<byte>(), 10, CancellationToken.None));
    }

    [Test]
    public void FindProvidersAsync_WithNegativeCount_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.FindProvidersAsync(new byte[] { 1, 2, 3 }, -1, CancellationToken.None));
    }

    [Test]
    public void FindProvidersAsync_WithZeroCount_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _protocol.FindProvidersAsync(new byte[] { 1, 2, 3 }, 0, CancellationToken.None));
    }

    [Test]
    public async Task PerformMaintenanceAsync_CleansUpExpiredValues()
    {
        // Arrange
        var expiredValue = new StoredValue
        {
            Value = new byte[] { 1, 2, 3 },
            Timestamp = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds(),
            Ttl = TimeSpan.FromHours(24),
            StoredAt = DateTime.UtcNow.AddHours(-25)
        };

        await _valueStore.PutValueAsync(new byte[] { 1 }, expiredValue);

        // Act
        await _protocol.PerformMaintenanceAsync(CancellationToken.None);

        // Assert
        Assert.That(_valueStore.Count, Is.EqualTo(0), "Expired values should be cleaned up");
    }

    [Test]
    public async Task GetStatistics_ReturnsCorrectStatistics()
    {
        // Arrange
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        byte[] value = Encoding.UTF8.GetBytes("test-value");
        await _protocol.PutValueAsync(key, value, CancellationToken.None);
        await _protocol.ProvideAsync(key, CancellationToken.None);

        // Act
        var stats = _protocol.GetStatistics();

        // Assert
        Assert.That(stats, Is.Not.Null);
        Assert.That(stats.ContainsKey("Mode"), Is.True);
        Assert.That(stats["Mode"], Is.EqualTo(KadDhtMode.Server.ToString()));
        Assert.That(stats.ContainsKey("StoredValues"), Is.True);
        Assert.That(stats["StoredValues"], Is.EqualTo(1));
        Assert.That(stats.ContainsKey("ProviderKeys"), Is.True);
        Assert.That(stats["ProviderKeys"], Is.EqualTo(1));
    }

    [Test]
    public void AddNode_WithValidNode_DoesNotThrow()
    {
        // Arrange
        var publicKey = new KademliaPublicKey(new byte[32]);
        var peerId = new PeerId(new byte[32]);
        var node = new DhtNode
        {
            PublicKey = publicKey,
            PeerId = peerId,
            Multiaddrs = Array.Empty<string>()
        };

        // Act & Assert
        Assert.DoesNotThrow(() => _protocol.AddNode(node));
    }

    [Test]
    public void AddNode_WithNullNode_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _protocol.AddNode(null!));
    }

    [Test]
    public async Task DialAsync_CompletesWithoutError()
    {
        // Arrange
        var mockChannel = Substitute.For<IChannel>();
        var mockContext = Substitute.For<ISessionContext>();
        mockContext.State.Returns(new State
        {
            RemoteAddress = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001")
        });

        // Act & Assert
        await _protocol.DialAsync(mockChannel, mockContext);
    }

    [Test]
    public async Task ListenAsync_CompletesWithoutError()
    {
        // Arrange
        var mockChannel = Substitute.For<IChannel>();
        var mockContext = Substitute.For<ISessionContext>();
        mockContext.State.Returns(new State
        {
            RemoteAddress = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001")
        });

        // Act & Assert
        await _protocol.ListenAsync(mockChannel, mockContext);
    }
}
