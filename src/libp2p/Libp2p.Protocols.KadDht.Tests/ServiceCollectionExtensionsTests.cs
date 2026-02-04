// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;
using KademliaPublicKey = global::Libp2p.Protocols.KadDht.Kademlia.PublicKey;
using KademliaMessageSender = global::Libp2p.Protocols.KadDht.Kademlia.IKademliaMessageSender<global::Libp2p.Protocols.KadDht.Kademlia.PublicKey, global::Libp2p.Protocols.KadDht.Integration.DhtNode>;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests;

[TestFixture]
public class ServiceCollectionExtensionsTests
{
    private IServiceCollection _services;
    private ILocalPeer _mockLocalPeer;

    [SetUp]
    public void Setup()
    {
        _services = new ServiceCollection();

        _mockLocalPeer = Substitute.For<ILocalPeer>();
        _mockLocalPeer.Identity.Returns(new Identity(new byte[32]));
        _mockLocalPeer.ListenAddresses.Returns(new System.Collections.ObjectModel.ObservableCollection<Multiformats.Address.Multiaddress>());

        _services.AddSingleton(_mockLocalPeer);
        _services.AddLogging();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _mockLocalPeer.DisposeAsync();
    }

    [Test]
    public void AddKadDht_RegistersAllRequiredServices()
    {

        _services.AddKadDht();
        using var serviceProvider = _services.BuildServiceProvider();

        // Assert - Check that all services are registered
        Assert.That(serviceProvider.GetService<KadDhtOptions>(), Is.Not.Null, "KadDhtOptions should be registered");
        Assert.That(serviceProvider.GetService<IValueStore>(), Is.Not.Null, "IValueStore should be registered");
        Assert.That(serviceProvider.GetService<IProviderStore>(), Is.Not.Null, "IProviderStore should be registered");
        Assert.That(serviceProvider.GetService<SharedDhtState>(), Is.Not.Null, "SharedDhtState should be registered");
        Assert.That(serviceProvider.GetService<KademliaMessageSender>(), Is.Not.Null, "IKademliaMessageSender should be registered");
        Assert.That(serviceProvider.GetService<KadDhtProtocol>(), Is.Not.Null, "KadDhtProtocol should be registered");
    }

    [Test]
    public void AddKadDht_WithCustomOptions_AppliesConfiguration()
    {
        var customKSize = 30;
        var customAlpha = 5;
        var customMode = KadDhtMode.Client;


        _services.AddKadDht(options =>
        {
            options.KSize = customKSize;
            options.Alpha = customAlpha;
            options.Mode = customMode;
        });

        using var serviceProvider = _services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<KadDhtOptions>();

        // Assert
        Assert.That(options.KSize, Is.EqualTo(customKSize), "Custom KSize should be applied");
        Assert.That(options.Alpha, Is.EqualTo(customAlpha), "Custom Alpha should be applied");
        Assert.That(options.Mode, Is.EqualTo(customMode), "Custom Mode should be applied");
    }

    [Test]
    public void AddKadDht_WithDefaultOptions_UsesDefaults()
    {

        _services.AddKadDht();
        using var serviceProvider = _services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<KadDhtOptions>();

        // Assert
        Assert.That(options.KSize, Is.EqualTo(20), "Default KSize should be 20");
        Assert.That(options.Alpha, Is.EqualTo(3), "Default Alpha should be 3");
        Assert.That(options.Mode, Is.EqualTo(KadDhtMode.Server), "Default Mode should be Server");
        Assert.That(options.RecordTtl, Is.EqualTo(TimeSpan.FromHours(24)), "Default RecordTtl should be 24 hours");
    }

    [Test]
    public void AddKadDht_RegistersInMemoryValueStore()
    {

        _services.AddKadDht(options => options.MaxStoredValues = 500);
        using var serviceProvider = _services.BuildServiceProvider();
        var valueStore = serviceProvider.GetRequiredService<IValueStore>();

        // Assert
        Assert.That(valueStore, Is.TypeOf<InMemoryValueStore>(), "Should register InMemoryValueStore");
        Assert.That(valueStore.Count, Is.EqualTo(0), "Store should start empty");
    }

    [Test]
    public void AddKadDht_RegistersServicesAsSingletons()
    {
        _services.AddKadDht();
        using var serviceProvider = _services.BuildServiceProvider();


        var options1 = serviceProvider.GetRequiredService<KadDhtOptions>();
        var options2 = serviceProvider.GetRequiredService<KadDhtOptions>();
        var sharedState1 = serviceProvider.GetRequiredService<SharedDhtState>();
        var sharedState2 = serviceProvider.GetRequiredService<SharedDhtState>();
        var protocol1 = serviceProvider.GetRequiredService<KadDhtProtocol>();
        var protocol2 = serviceProvider.GetRequiredService<KadDhtProtocol>();

        // Assert - Should be the same instances
        Assert.That(ReferenceEquals(options1, options2), Is.True, "KadDhtOptions should be singleton");
        Assert.That(ReferenceEquals(sharedState1, sharedState2), Is.True, "SharedDhtState should be singleton");
        Assert.That(ReferenceEquals(protocol1, protocol2), Is.True, "KadDhtProtocol should be singleton");
    }

    [Test]
    public void WithKadDht_RegistersProtocolHandlers()
    {
        _services.AddKadDht();
        using var serviceProvider = _services.BuildServiceProvider();
        var builder = new TestPeerFactoryBuilder(serviceProvider);


        var result = builder.WithKadDht();

        // Assert
        Assert.That(result, Is.SameAs(builder), "Should return the builder instance");

        var registeredProtocolIds = builder.Protocols.Select(p => p.Id).ToArray();
        var expectedProtocolIds = new[]
        {
            "/ipfs/kad/1.0.0/ping",
            "/ipfs/kad/1.0.0/find_node",
            "/ipfs/kad/1.0.0/put_value",
            "/ipfs/kad/1.0.0/get_value",
            "/ipfs/kad/1.0.0/add_provider",
            "/ipfs/kad/1.0.0/get_providers"
        };

        Assert.That(registeredProtocolIds, Is.EqualTo(expectedProtocolIds));
    }

    [Test]
    public void AddKadDht_CanResolveAllDependenciesWithoutErrors()
    {

        _services.AddKadDht();
        using var serviceProvider = _services.BuildServiceProvider();

        // Assert - Should not throw
        Assert.DoesNotThrow(() =>
        {
            var options = serviceProvider.GetRequiredService<KadDhtOptions>();
            var valueStore = serviceProvider.GetRequiredService<IValueStore>();
            var providerStore = serviceProvider.GetRequiredService<IProviderStore>();
            var sharedState = serviceProvider.GetRequiredService<SharedDhtState>();
            var messageSender = serviceProvider.GetRequiredService<KademliaMessageSender>();
            var protocol = serviceProvider.GetRequiredService<KadDhtProtocol>();
        });
    }

    private sealed class TestPeerFactoryBuilder : ILibp2pPeerFactoryBuilder
    {
        private readonly List<IProtocol> _protocols = new();

        public TestPeerFactoryBuilder(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            Services = new ServiceCollection();
        }

        public IReadOnlyList<IProtocol> Protocols => _protocols;

        public IServiceProvider ServiceProvider { get; }

        public IServiceCollection Services { get; }

        public IPeerFactoryBuilder AddProtocol<TProtocol>(TProtocol? instance = default, bool isExposed = true)
            where TProtocol : IProtocol
        {
            if (instance is null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            _protocols.Add(instance);
            return this;
        }

        public IPeerFactory Build() => throw new NotSupportedException();

        public ILibp2pPeerFactoryBuilder WithPlaintextEnforced() => this;

        public ILibp2pPeerFactoryBuilder WithPubsub() => this;

        public ILibp2pPeerFactoryBuilder WithRelay() => this;

        public ILibp2pPeerFactoryBuilder WithQuic() => this;
    }
}
