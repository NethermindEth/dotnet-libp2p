// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.DependencyInjection;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using Multiformats.Address;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.WebRtc;
using Nethermind.Libp2p.Protocols.WebRtc.Extensions;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class KadBuilderTests
{
    [Test]
    public async Task BuilderCreatesPeerBoundDhtWithoutLocalPeerRegistration()
    {
        await using var provider = new ServiceCollection()
            .AddLibp2p(builder => builder.AddKadDht())
            .BuildServiceProvider();
        var factory = provider.GetRequiredService<IPeerFactory>();
        await using var first = factory.Create();
        await using var second = factory.Create();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(first.GetKadDht)));
        Assert.That(concurrent.All(runtime => ReferenceEquals(runtime, concurrent[0])), Is.True);
        Assert.That(first.GetKadDht(), Is.Not.Null);
        Assert.That(((LocalPeer)first).GetProtocol<KadDhtProtocol>(KadDhtOptions.DefaultProtocolId), Is.SameAs(first.GetKadDht()));
        Assert.That(((LocalPeer)first).GetProtocol<KadDhtProtocol>("/unregistered/kad/1.0.0"), Is.Null);
        Assert.That(second.GetKadDht(), Is.Not.SameAs(first.GetKadDht()));
        Assert.That(first.GetKadDht()!.GetStatistics()["LocalPeerId"], Is.EqualTo(first.Identity.PeerId.ToString()));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SuppliedDhtAliasDoesNotHoldBuilderLockWhileWaitingForPeer(bool enumerable, bool nestedFactory)
    {
        using var aliasEntered = new ManualResetEventSlim();
        var services = new ServiceCollection().AddKadDht();
        var registration = services.Single(d => d.ServiceType == typeof(SharedDhtState));
        services.Remove(registration);
        services.AddSingleton(sp =>
        {
            aliasEntered.Set();
            return (SharedDhtState)registration.ImplementationFactory!(sp);
        });
        services.AddLibp2p(builder =>
        {
            builder.AddKadDht();
            builder.Services.AddSingleton(sp => new StateConsumer(ResolveState(sp)));
            return builder;
        });
        services.AddSingleton<ILocalPeer>(sp => sp.GetRequiredService<IPeerFactory>().Create());
        await using var provider = services.BuildServiceProvider();
        var peer = (LocalPeer)provider.GetRequiredService<ILocalPeer>();
        var resolver = provider.GetRequiredService<ILibp2pPeerFactoryBuilder>().ServiceProvider;
        var options = resolver.GetRequiredService<KadDhtOptions>();
        var aliasResult = new TaskCompletionSource<SharedDhtState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionsResult = new TaskCompletionSource<KadDhtOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread aliasThread = Worker(() => nestedFactory ? resolver.GetRequiredService<StateConsumer>().State : ResolveState(resolver), aliasResult);
        Thread optionsThread = Worker(resolver.GetRequiredService<KadDhtOptions>, optionsResult);
        bool aliasStarted = false;
        bool optionsStarted = false;
        bool optionsProgressed = false;
        try
        {
            lock (peer.Sessions)
            {
                aliasThread.Start();
                aliasStarted = true;
                Assert.That(aliasEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                // The production alias must reach the peer lock before testing the other resolver.
                Assert.That(SpinWait.SpinUntil(() => Waiting(aliasThread), TimeSpan.FromSeconds(5)), Is.True);
                optionsThread.Start();
                optionsStarted = true;
                Assert.That(SpinWait.SpinUntil(() => optionsResult.Task.IsCompleted || Waiting(optionsThread), TimeSpan.FromSeconds(5)), Is.True);
                optionsProgressed = optionsResult.Task.IsCompleted;
            }
        }
        finally
        {
            // Release the peer lock before joining, including when the old container blocks both threads.
            if (aliasStarted) Assert.That(aliasThread.Join(TimeSpan.FromSeconds(10)), Is.True);
            if (optionsStarted) Assert.That(optionsThread.Join(TimeSpan.FromSeconds(10)), Is.True);
        }

        Assert.That(optionsProgressed, Is.True, "A peer alias must not block unrelated builder dependencies while waiting for peer initialization.");
        Assert.That(await optionsResult.Task, Is.SameAs(options));
        Assert.That(await aliasResult.Task, Is.SameAs(provider.GetRequiredService<SharedDhtState>()));

        SharedDhtState ResolveState(IServiceProvider sp) => enumerable ? sp.GetServices<SharedDhtState>().Single() : sp.GetRequiredService<SharedDhtState>();
        static bool Waiting(Thread thread) => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
        static Thread Worker<T>(Func<T> resolve, TaskCompletionSource<T> result) => new(() =>
        {
            try { result.TrySetResult(resolve()); }
            catch (Exception ex) { result.TrySetException(ex); }
        })
        { IsBackground = true };
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PeerBoundFindNodeReplacesRequesterBeforeRoutingLimit(bool serviceRegistration)
    {
        var services = new ServiceCollection();
        if (serviceRegistration) services.AddKadDht(options => options.KSize = 3);
        services.AddLibp2p(builder => serviceRegistration
            ? builder.WithKadDht()
            : builder.AddKadDht(options => options.KSize = 3));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IPeerFactory>();
        await using var server = factory.Create();
        var routing = server.GetKadDht()!.RoutingTable!;
        var identities = Enumerable.Range(1, 64)
            .Select(i => new Identity(Enumerable.Repeat((byte)i, 32).ToArray()))
            // Keep both root buckets below K so this fixture does not launch eviction probes.
            .GroupBy(identity => PublicKey.ComputeHash(identity.PeerId.Bytes).Bytes[0] & 0x80)
            .SelectMany(group => group.Take(2)).ToArray();
        Assert.That(identities, Has.Length.EqualTo(4));
        for (int i = 0; i < identities.Length; i++)
            server.GetKadDht()!.AddNode(identities[i].PeerId.ToDhtNode([$"/ip4/127.0.0.1/tcp/{4001 + i}"]));
        var target = new PublicKey([1]);
        var requester = routing.GetKNearestNeighbour(target.Hash, true)[0];
        var expected = routing.GetKNearestNeighbourExcluding(target.Hash, requester.PublicKey.Hash, true);
        Assert.That(expected, Has.Length.EqualTo(3));
        await using var client = factory.Create(identities.Single(identity => identity.PeerId.Equals(requester.PeerId)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], deadline.Token);
        var session = await client.DialAsync(server.ListenAddresses.ToArray(), deadline.Token);

        var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            MessageHelper.CreateFindNodeRequest([1]), deadline.Token);

        Assert.That(response.CloserPeers, Has.Count.EqualTo(3));
        Assert.That(response.CloserPeers.Select(peer => new PeerId(peer.Id.ToByteArray())), Does.Not.Contain(requester.PeerId));
        Assert.That(response.CloserPeers.Select(peer => new PeerId(peer.Id.ToByteArray())),
            Is.EqualTo(expected.Select(node => node.PeerId)));
        Assert.That(response.CloserPeers.Select(peer => Multiaddress.Decode(peer.Addrs.Single().ToByteArray()).ToString()),
            Is.EqualTo(expected.Select(node => node.Multiaddrs.Single())));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task RegisteredWireHandlerSharesClientStoreAndHonorsCapacity(bool serviceRegistration, bool customId)
    {
        string id = customId ? "/test/kad/1.0.0" : KadDhtOptions.DefaultProtocolId;
        void Configure(KadDhtOptions options)
        {
            options.ProtocolId = id;
            options.MaxStoredValues = 1;
            options.MaxProvidersPerKey = 1;
            options.MaxProviderKeys = 1;
            options.MaxAddressesPerProvider = 1;
            options.MaxProviderRecordBytes = 512;
            options.MaxPeerResponseBytes = 1024;
        }
        var services = new ServiceCollection();
        if (serviceRegistration) services.AddKadDht(Configure);
        services.AddLibp2p(builder => serviceRegistration ? builder.WithKadDht() : builder.AddKadDht(Configure));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IPeerFactory>();
        await using var server = factory.Create();
        await using var client = factory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], deadline.Token);
        var stack = provider.GetRequiredService<IProtocolStackSettings>();
        Assert.That(stack.Protocols!.Keys.Count(p => p.Id == id), Is.EqualTo(1));
        Assert.That(stack.Protocols.Keys.Any(p => p.Protocol is KadDhtProtocol), Is.False);
        var builder = provider.GetRequiredService<ILibp2pPeerFactoryBuilder>();
        Assert.That(builder.ServiceProvider.GetRequiredService<KadDhtOptions>().MaxProvidersPerKey, Is.EqualTo(1));
        Assert.That(builder.ServiceProvider.GetRequiredService<KadDhtOptions>().MaxPeerResponseBytes, Is.EqualTo(1024));
        var localClient = ((IPeerServices)server).GetPeerService<DhtClient>()!;
        Assert.That(await localClient.PutValueAsync("one", "first", deadline.Token), Is.EqualTo(1));
        var sender = new LibP2pKademliaMessageSender(new KadDhtOptions { ProtocolId = id }, client);
        var receiver = server.Identity.PeerId.ToDhtNode(server.ListenAddresses.Select(a => a.ToString()));
        Assert.That((await sender.GetValueAsync(receiver, "one"u8.ToArray(), deadline.Token)).Value, Is.EqualTo("first"u8.ToArray()));
        Assert.That(await sender.PutValueAsync(receiver, "two"u8.ToArray(), "second"u8.ToArray(), deadline.Token), Is.False);
        Assert.That(await sender.PutValueAsync(receiver, "one"u8.ToArray(), "replacement"u8.ToArray(), deadline.Token), Is.True);
        Assert.That(await localClient.GetValueAsync("one", deadline.Token), Is.EqualTo((true, "replacement")));
        Assert.That(await localClient.PutValueAsync("two", "second", deadline.Token), Is.LessThanOrEqualTo(1));
        var values = builder.ServiceProvider.GetRequiredService<IValueStore>();
        Assert.That(values.Count, Is.EqualTo(1));
        Assert.That(await values.GetValueAsync("two"u8.ToArray(), deadline.Token), Is.Null);
        // Content inserted through a custom/legacy path is revalidated on both local and wire reads.
        await values.RemoveValueAsync("one"u8.ToArray(), deadline.Token);
        byte[] ipns = "/ipns/unsigned"u8.ToArray();
        await values.PutValueAsync(ipns, new StoredValue { Value = "unsigned"u8.ToArray(), Ttl = TimeSpan.FromHours(1) }, deadline.Token);
        Assert.That(await server.GetKadDht()!.GetValueAsync(ipns, deadline.Token), Is.Null);
        Assert.That((await sender.GetValueAsync(receiver, ipns, deadline.Token)).HasValue, Is.False);
        Assert.That(await sender.PutValueAsync(receiver, ipns, "unsigned"u8.ToArray(), deadline.Token), Is.False);
        await values.RemoveValueAsync(ipns, deadline.Token);
        var signer = new Identity();
        byte[] signedKey = IpnsRecordHelper.BuildIpnsKey(signer.PeerId);
        byte[] signedValue = IpnsRecordHelper.CreateSignedRecord(signer, "/ipfs/test", 1, DateTimeOffset.UtcNow.AddHours(1));
        Assert.That(await sender.PutValueAsync(receiver, signedKey, signedValue, deadline.Token), Is.True);
        Assert.That((await sender.GetValueAsync(receiver, signedKey, deadline.Token)).Value, Is.EqualTo(signedValue));
        Assert.That(await server.GetKadDht()!.GetValueAsync(signedKey, deadline.Token), Is.EqualTo(signedValue));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await server.GetKadDht()!.GetValueAsync(signedKey, cancelled.Token));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitStoresOverrideRegistrationsAndSingletonPeerUsesSameRuntime(bool explicitStores)
    {
        var registered = new InMemoryValueStore(1);
        var supplied = new InMemoryValueStore(1);
        var providers = new InMemoryProviderStore(1, 1, 1, 512);
        var services = new ServiceCollection().AddSingleton<IValueStore>(registered).AddSingleton<IProviderStore>(providers);
        services.AddKadDht();
        services.AddLibp2p(builder => builder.AddKadDht(valueStore: explicitStores ? supplied : null));
        services.AddSingleton(sp => sp.GetRequiredService<IPeerFactory>().Create());
        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<ILocalPeer>();
        var runtime = server.GetKadDht()!;
        Assert.That(provider.GetRequiredService<KadDhtProtocol>(), Is.SameAs(runtime));
        Assert.That(provider.GetRequiredService<DhtClient>(), Is.SameAs(((IPeerServices)server).GetPeerService<DhtClient>()));
        var builder = provider.GetRequiredService<ILibp2pPeerFactoryBuilder>();
        Assert.That(builder.ServiceProvider.GetRequiredService<IValueStore>(), Is.SameAs(explicitStores ? supplied : registered));
        Assert.That(builder.ServiceProvider.GetRequiredService<IProviderStore>(), Is.SameAs(providers));
        Assert.That(builder.Services.Any(descriptor => descriptor.ServiceType.ToString().Contains("TestNode", StringComparison.Ordinal)), Is.False);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], deadline.Token);
        await using var remote = provider.GetRequiredService<IPeerFactory>().Create();
        var sender = new LibP2pKademliaMessageSender(remote);
        var receiver = server.Identity.PeerId.ToDhtNode(server.ListenAddresses.Select(address => address.ToString()));
        Assert.That(await sender.PutValueAsync(receiver, [1], [10], deadline.Token), Is.True);
        Assert.That(await sender.PutValueAsync(receiver, [2], [20], deadline.Token), Is.False);
        Assert.That((await (explicitStores ? supplied : registered).GetValueAsync(new byte[] { 1 }, deadline.Token))!.Value, Is.EqualTo(new byte[] { 10 }));
        await server.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => runtime.AddNode(remote.Identity.PeerId.ToDhtNode()));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BuilderServicesResolveNestedDependenciesAndFreeze(bool supplied)
    {
        await using var provider = new ServiceCollection().AddLibp2p().AddSingleton<Dependency>().BuildServiceProvider();
        await using var builder = new Libp2pPeerFactoryBuilder(supplied ? provider : null);
        Dependency dependency = supplied ? provider.GetRequiredService<Dependency>() : new Dependency();
        if (!supplied) builder.Services.AddSingleton(dependency);
        builder.Services.AddSingleton<Dependent>();
        builder.Services.AddSingleton<IBorrowedService>(sp => sp.GetRequiredService<Dependency>());
        builder.Services.AddSingleton(sp => new FactoryDependent(sp.GetRequiredService<Dependent>()));
        builder.Services.AddScoped<ScopedDependency>();
        builder.Services.AddTransient<TransientDependency>();
        builder.Services.AddSingleton(typeof(GenericDependency<>));
        builder.AddProtocol<DependentProtocol>();
        var explicitProtocol = new ExplicitProtocol();
        var limit = new InboundStreamLimit(2);
        builder.AddProtocol(explicitProtocol, false, limit);
        builder.AddWebRtcDirect();
        var factory = builder.Build();
        Assert.That(builder.Build(), Is.SameAs(factory));
        Assert.That(builder.ServiceProvider.GetRequiredService<Dependent>().Dependency, Is.SameAs(dependency));
        Assert.That(builder.ServiceProvider.GetRequiredService<FactoryDependent>().Value, Is.SameAs(builder.ServiceProvider.GetRequiredService<Dependent>()));
        Assert.That(builder.ServiceProvider.GetRequiredService<GenericDependency<int>>().Dependency, Is.SameAs(dependency));
        using (var scope = builder.ServiceProvider.CreateScope())
        {
            var scoped = scope.ServiceProvider.GetRequiredService<ScopedDependency>();
            Assert.That(scope.ServiceProvider.GetRequiredService<ScopedDependency>(), Is.SameAs(scoped));
            Assert.That(scope.ServiceProvider.GetRequiredService<ScopedDependency>(), Is.Not.SameAs(builder.ServiceProvider.GetRequiredService<ScopedDependency>()));
            var transient = scope.ServiceProvider.GetRequiredService<TransientDependency>();
            Assert.That(scope.ServiceProvider.GetRequiredService<TransientDependency>(), Is.Not.SameAs(transient));
        }
        var stack = builder.ServiceProvider.GetRequiredService<IProtocolStackSettings>();
        var registration = stack.Protocols!.Keys.Single(p => ReferenceEquals(p.Protocol, explicitProtocol));
        Assert.That(registration.IsExposed, Is.False);
        Assert.That(registration.InboundLimit, Is.SameAs(limit));
        Assert.That(stack.TopProtocols!.Single(p => p.Protocol is WebRtcDirectProtocol).Protocol,
            Is.SameAs(builder.ServiceProvider.GetRequiredService<WebRtcDirectProtocol>()));
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddSingleton(new Dependency()));
        Assert.Throws<InvalidOperationException>(() => builder.AddProtocol(new ExplicitProtocol()));
        Assert.That(builder.ServiceProvider.GetRequiredService<IBorrowedService>(), Is.SameAs(dependency));
        var owned = builder.ServiceProvider.GetRequiredService<Dependent>();
        await builder.DisposeAsync();
        Assert.That(owned.Disposed, Is.True);
        Assert.That(dependency.Disposed, Is.False);
        Assert.That(explicitProtocol.Disposed, Is.False);
    }

    [Test]
    public async Task OutboundLookupValidatesRecordsFromNegotiatedPeer()
    {
        var signer = new Identity();
        byte[] key = IpnsRecordHelper.BuildIpnsKey(signer.PeerId);
        byte[] answer = "unsigned"u8.ToArray();
        await using var maliciousProvider = new ServiceCollection().AddLibp2p(builder =>
            builder.AddRequestResponseProtocol<Message, Message>(KadDhtOptions.DefaultProtocolId, (request, _) =>
                Task.FromResult(MessageHelper.CreateGetValueResponse(MessageHelper.CreatePutValueRequest(request.Key.ToByteArray(), answer).Record))))
            .BuildServiceProvider();
        await using var malicious = maliciousProvider.GetRequiredService<IPeerFactory>().Create();
        await using var honestProvider = new ServiceCollection().AddLibp2p(builder => builder.AddKadDht()).BuildServiceProvider();
        await using var honest = honestProvider.GetRequiredService<IPeerFactory>().Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await malicious.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], deadline.Token);
        var protocol = honest.GetKadDht()!;
        protocol.AddNode(malicious.Identity.PeerId.ToDhtNode(malicious.ListenAddresses.Select(address => address.ToString())));
        Assert.That(await protocol.GetValueAsync(key, deadline.Token), Is.Null);
        answer = IpnsRecordHelper.CreateSignedRecord(signer, "/ipfs/test", 1, DateTimeOffset.UtcNow.AddHours(1));
        Assert.That(await protocol.GetValueAsync(key, deadline.Token), Is.EqualTo(answer));
    }

    [Test]
    public async Task ClientModeCanDialButDoesNotAcceptDhtNegotiation()
    {
        await using var serverProvider = new ServiceCollection().AddLibp2p(builder => builder.AddKadDht()).BuildServiceProvider();
        await using var clientProvider = new ServiceCollection().AddLibp2p(builder => builder.AddKadDht(options => options.Mode = KadDhtMode.Client)).BuildServiceProvider();
        await using var server = serverProvider.GetRequiredService<IPeerFactory>().Create();
        await using var client = clientProvider.GetRequiredService<IPeerFactory>().Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], deadline.Token);
        await client.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], deadline.Token);
        var clientStack = clientProvider.GetRequiredService<IProtocolStackSettings>();
        var registration = clientStack.Protocols!.Keys.Single(p => p.Id == KadDhtOptions.DefaultProtocolId);
        Assert.That(registration.IsExposed, Is.False);
        Assert.That(registration.AcceptInbound, Is.False);
        var sender = new LibP2pKademliaMessageSender(client);
        Assert.That(await sender.PutValueAsync(server.Identity.PeerId.ToDhtNode(server.ListenAddresses.Select(address => address.ToString())), [1], [10], deadline.Token), Is.True);
        var reverse = new LibP2pKademliaMessageSender(server);
        var clientNode = client.Identity.PeerId.ToDhtNode(client.ListenAddresses.Select(address => address.ToString()));
        Assert.That(async () => await reverse.GetValueAsync(clientNode, [1], deadline.Token), Throws.TypeOf<Nethermind.Libp2p.Core.Exceptions.ChannelClosedException>());
    }

    [Test]
    [Obsolete("Compiles and exercises public compatibility APIs from an assembly without friend access.")]
    public async Task LegacyPublicConsumerRemainsSourceCompatible()
    {
        var peerId = new Identity().PeerId;
        var node = new global::Libp2p.Protocols.KadDht.TestNode(peerId);
        var simulation = global::Libp2p.Protocols.KadDht.Kademlia.TestNode.ForSimulation(new PublicKey(peerId.Bytes));
        Assert.That(simulation.Id.Bytes.ToArray(), Is.EqualTo(peerId.Bytes.ToArray()));
        var book = new global::Libp2p.Protocols.KadDht.Transport.TestNodeAddressBook();
        var address = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001");
        book.SetAddresses(node, address);
        Assert.That(book.TryGet(node), Is.EqualTo(new[] { address }));
        Assert.That(new PublicKeyKeyOperator().GetKey(node).Bytes.ToArray(), Is.EqualTo(peerId.Bytes.ToArray()));
        var local = Substitute.For<ILocalPeer>();
        await using var sender = new global::Libp2p.Protocols.KadDht.Network.LibP2pKademliaMessageSender<PublicKey, DhtNode>(local);
        await using var registered = new ServiceCollection().AddSingleton(local).AddKadDht().BuildServiceProvider();
        var state = registered.GetRequiredService<SharedDhtState>();
        state.LocalPeerKey = new PublicKey(peerId.Bytes);
        using var client = new DhtClient(state, sender);
        Assert.That(await client.PutValueAsync("key", "value"), Is.EqualTo(1));
        Assert.That(state.ValueStore.Get("key"u8.ToArray())!.Value, Is.EqualTo("value"u8.ToArray()));
        Assert.That(state.ValueStore.Get("key"u8.ToArray())!.Publisher, Is.EqualTo(peerId.Bytes.ToArray()));
        Assert.That((await registered.GetRequiredService<IValueStore>().GetValueAsync("key"u8.ToArray()))!.Value,
            Is.EqualTo("value"u8.ToArray()));
        var sessionSender = Substitute.For<global::Libp2p.Protocols.KadDht.IKademliaMessageSender<PublicKey, global::Libp2p.Protocols.KadDht.TestNode>>();
        ISessionManager manager = new KademliaSessionManager(new SessionOptions(), peerId, sessionSender);
        Assert.That(await manager.DiscoverAsync<global::Libp2p.Protocols.KadDht.TestNode>(new PublicKey(peerId.Bytes), CancellationToken.None), Is.Empty);
        await manager.StopAsync(CancellationToken.None);
    }

    [Test]
    public void FailedBuildCanBeConfiguredAndRetried()
    {
        using var builder = new Libp2pPeerFactoryBuilder();
        builder.AddProtocol<DependentProtocol>();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        builder.Services.AddSingleton<Dependency>();
        builder.Services.AddSingleton<Dependent>();
        builder.Services.AddSingleton<FactoryDependent>();
        Assert.That(builder.Build(), Is.Not.Null);
        Assert.Throws<InvalidOperationException>(() => builder.WithPubsub());
    }

    [Test]
    public async Task FailedBuildDiscardsWiredStackAndDisposedProtocolsBeforeRetry()
    {
        await using var builder = new FailingStackBuilder();
        var constructed = new List<RetryProtocol>();
        builder.Services.AddTransient(_ =>
        {
            var protocol = new RetryProtocol();
            constructed.Add(protocol);
            return protocol;
        });
        builder.AddProtocol<RetryProtocol>();

        Assert.That(Assert.Throws<InvalidOperationException>(() => builder.Build()), Is.SameAs(builder.Failure));
        Assert.That(builder.StackBuilds, Is.EqualTo(1));
        Assert.That(constructed, Has.Count.EqualTo(1));
        var failedProtocol = constructed.Single();
        Assert.That(failedProtocol.Disposals, Is.EqualTo(1));

        Assert.That(builder.Build(), Is.Not.Null);
        Assert.That(builder.StackBuilds, Is.EqualTo(2));
        Assert.That(constructed, Has.Count.EqualTo(2));
        var retryProtocol = constructed[1];
        Assert.That(retryProtocol, Is.Not.SameAs(failedProtocol));
        Assert.That(retryProtocol.Disposals, Is.Zero);
        var stack = builder.ServiceProvider.GetRequiredService<IProtocolStackSettings>();
        Assert.That(stack.Protocols!.Keys.Count(p => p.Protocol is RetryProtocol), Is.EqualTo(1));
        Assert.That(stack.Protocols.Keys.Single(p => p.Protocol is RetryProtocol).Protocol, Is.SameAs(retryProtocol));
        Assert.That(stack.Protocols.Keys.Select(p => p.Protocol), Does.Not.Contain(failedProtocol));

        await builder.DisposeAsync();
        Assert.That(failedProtocol.Disposals, Is.EqualTo(1));
        Assert.That(retryProtocol.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task PeerServiceAliasesHaveOneReleaseOwner()
    {
        await using var builder = new Libp2pPeerFactoryBuilder();
        builder.AddPeerService<PeerResourceOwner>((_, _) => new PeerResourceOwner());
        builder.AddPeerService<PeerResource>((peer, _) => ((IPeerServices)peer).GetPeerService<PeerResourceOwner>()!.Resource, ownsInstance: false);
        await using var peer = builder.Build().Create();
        var resource = ((IPeerServices)peer).GetPeerService<PeerResource>()!;
        await peer.DisposeAsync();
        Assert.That(resource.Disposals, Is.EqualTo(1));
    }

    public sealed class PeerResource : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    public sealed class PeerResourceOwner : IDisposable
    {
        public PeerResource Resource { get; } = new();
        public void Dispose() => Resource.Dispose();
    }

    public interface IBorrowedService : IDisposable;
    public sealed class Dependency : IBorrowedService
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    public sealed class Dependent(Dependency dependency) : IDisposable
    {
        public Dependency Dependency => dependency;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    public sealed class FactoryDependent(Dependent value) { public Dependent Value => value; }
    public sealed class GenericDependency<T>(Dependency dependency) { public Dependency Dependency => dependency; }
    public sealed class ScopedDependency;
    public sealed class TransientDependency;
    public sealed class StateConsumer(SharedDhtState state) { public SharedDhtState State => state; }
    private sealed class FailingStackBuilder : Libp2pPeerFactoryBuilder
    {
        public InvalidOperationException Failure { get; } = new("Simulated failure after wiring the protocol stack.");
        public int StackBuilds { get; private set; }

        protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
        {
            var stack = base.BuildStack(additionalProtocols);
            if (++StackBuilds == 1) throw Failure;
            return stack;
        }
    }
    public sealed class RetryProtocol : IProtocol, IDisposable
    {
        public string Id => "/retry";
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    public sealed class DependentProtocol(FactoryDependent dependency) : IProtocol
    {
        public string Id => dependency.Value.Dependency is not null ? "/dependent" : throw new InvalidOperationException();
    }
    public sealed class ExplicitProtocol : IProtocol, IDisposable
    {
        public string Id => "/explicit";
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
