// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class PeerStoreWiringTests
{
    [Test]
    public async Task DefaultStoreIsRegisteredUnderItsContract()
    {
        await using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        _ = provider.GetRequiredService<IPeerFactory>();
        IPeerStore store = provider.GetServices<IPeerStore>().Single();
        Assert.That(store, Is.TypeOf<PeerStore>());
        Assert.That(provider.GetRequiredService<ILibp2pPeerFactoryBuilder>().ServiceProvider.GetRequiredService<IPeerStore>(), Is.SameAs(store));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StandaloneBuilderReusesOneStore(bool replaceDefault)
    {
        await using Libp2pPeerFactoryBuilder builder = new();
        builder.Services.AddPubsub();
        RecordingStore replacement = new();
        if (replaceDefault) builder.Services.TryAddSingleton<IPeerStore>(replacement);
        builder.WithPubsub();

        await using ILocalPeer peer = builder.Build().Create();
        IPeerStore store = builder.ServiceProvider.GetServices<IPeerStore>().Single();
        Assert.That(store, replaceDefault ? Is.SameAs(replacement) : Is.TypeOf<PeerStore>());
        Assert.That(builder.ServiceProvider.GetRequiredService<IPeerStore>(), Is.SameAs(store));

        LocalPeer.Session session = new((LocalPeer)peer) { State = { RemoteAddress = TestPeers.Multiaddr(2) } };
        session.RegisterSession();
        try
        {
            Assert.That(store.TryGetPeerInfo(session.RemotePeerId, out _), Is.True);
        }
        finally { await session.DisconnectAsync(); }
    }

    [Test]
    public async Task FailedBuildAllowsRegisteringAReplacementStoreBeforeRetrying()
    {
        await using Libp2pPeerFactoryBuilder builder = new();
        builder.WithPubsub();
        Assert.Throws<InvalidOperationException>(() => builder.Build());

        RecordingStore replacement = new();
        builder.Services.TryAddSingleton<IPeerStore>(replacement);
        builder.Services.AddPubsub();
        _ = builder.Build();

        Assert.That(builder.ServiceProvider.GetRequiredService<IPeerStore>(), Is.SameAs(replacement));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HostBuilderRejectsPeerStoreRegisteredInItsCallback(bool genericRegistration)
    {
        ServiceCollection services = new();
        if (genericRegistration)
            services.AddLibp2p<DhtBuilder>(builder => Configure((ILibp2pPeerFactoryBuilder)builder));
        else
            services.AddLibp2p(Configure);

        await using ServiceProvider provider = services.BuildServiceProvider();
        Assert.That(() => provider.GetRequiredService<IPeerFactory>(),
            Throws.InvalidOperationException.With.Message.Contains("Register the replacement IPeerStore on the host service collection"));

        static IPeerFactoryBuilder Configure(ILibp2pPeerFactoryBuilder builder)
        {
            builder.Services.AddSingleton<IPeerStore, RecordingStore>();
            return builder;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StackAndHostProtocolsUseTheRegisteredReplacement(bool genericRegistration)
    {
        RecordingStore store = new();
        ServiceCollection services = new();
        services.AddPubsub();
        services.AddSingleton(new PubsubSettings { ReconnectionPeriod = int.MaxValue });
        if (genericRegistration)
        {
            services.AddLibp2p<DhtBuilder>(builder => ((ILibp2pPeerFactoryBuilder)builder).WithPubsub().WithKadDht());
            services.Replace(ServiceDescriptor.Singleton<IPeerStore>(store));
        }
        else
        {
            services.AddSingleton<IPeerStore>(store);
            services.AddLibp2p(builder => builder.WithPubsub().WithKadDht());
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using ILocalPeer peer = provider.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));
        var builder = (ILibp2pPeerFactoryBuilder)provider.GetRequiredService<IPeerFactoryBuilder>();
        Assert.That(provider.GetServices<IPeerStore>().Single(), Is.SameAs(store));
        Assert.That(builder.ServiceProvider.GetRequiredService<IPeerStore>(), Is.SameAs(store));
        _ = provider.GetRequiredService<MDnsDiscoveryProtocol>();

        PeerId unknown = TestPeers.PeerId(4);
        Assert.ThrowsAsync<Libp2pException>(async () => await peer.DialAsync(unknown));
        Assert.That(store.LastLookup, Is.EqualTo(unknown));
        Assert.That(store.TryGetPeerInfo(unknown, out PeerMetadata? missing), Is.False);
        Assert.That(missing, Is.Null);

        Identity identified = TestPeers.Identity(2);
        Multiaddress identifiedAddress = $"/ip4/192.0.2.20/tcp/4001/p2p/{identified.PeerId}";
        LocalPeer.Session session = new((LocalPeer)peer) { State = { RemoteAddress = identifiedAddress } };
        session.RegisterSession();
        try
        {
            Assert.That(store.Retentions, Is.EqualTo(1));
            Assert.That(store.TryGetPeerInfo(identified.PeerId, out _), Is.True);
        }
        finally { await session.DisconnectAsync(); }
        Assert.That(store.Releases, Is.EqualTo(1));

        IProtocol[] protocols = provider.GetRequiredService<IProtocolStackSettings>().Protocols!.Keys.Select(p => p.Protocol).ToArray();
        ISessionContext context = Substitute.For<ISessionContext>();
        context.Peer.Returns(peer);
        context.State.Returns(new State { RemoteAddress = identifiedAddress, RemotePublicKey = identified.PublicKey });
        ByteString record = SigningHelper.CreateSignedEnvelope(identified, [identifiedAddress], 42);
        Identify message = new()
        {
            PublicKey = identified.PublicKey.ToByteString(),
            AgentVersion = "replacement/1",
            Protocols = { PubsubRouter.GossipsubProtocolVersionV13 },
            SignedPeerRecord = record
        };
        Channel identifyChannel = new();
        Task writer = identifyChannel.Reverse.WriteSizeAndProtobufAsync(message).AsTask();
        try
        {
            await protocols.OfType<IdentifyProtocol>().Single().DialAsync(identifyChannel, context);
        }
        finally
        {
            await identifyChannel.CloseAsync();
            await writer;
        }

        PeerMetadata metadata = store.ReadPeerInfo(identified.PeerId);
        Assert.That(metadata.AgentVersion, Is.EqualTo("replacement/1"));
        Assert.That(metadata.SupportedProtocols, Is.EqualTo(new[] { PubsubRouter.GossipsubProtocolVersionV13 }));
        Assert.That(metadata.SignedPeerRecord, Is.EqualTo(record));
        Assert.That(metadata.PeerRecordSequence, Is.EqualTo(42UL));
        Assert.That(metadata.Addresses, Is.EqualTo(new[] { identifiedAddress }));

        ((IPeerServices)peer).GetPeerService<KadDhtProtocol>()!.AddNode(identified.PeerId.ToDhtNode());
        var dht = protocols.OfType<RequestResponseProtocol<Message, Message>>().Single();
        Multiaddress requester = TestPeers.Multiaddr(3);
        context.State.Returns(new State { RemoteAddress = requester });
        Channel dhtChannel = new();
        Task listener = dht.ListenAsync(dhtChannel.Reverse, context);
        Message response;
        try
        {
            response = await dht.DialAsync(dhtChannel, context, MessageHelper.CreateFindNodeRequest(identified.PeerId.Bytes));
        }
        finally
        {
            await dhtChannel.CloseAsync();
            await listener;
        }

        Assert.That(store.ReadPeerInfo(requester.GetPeerId()!).Addresses, Is.EqualTo(new[] { requester }));
        Message.Types.Peer nearest = response.CloserPeers.Single();
        Assert.That(nearest.Id.ToByteArray(), Is.EqualTo(identified.PeerId.Bytes));
        Assert.That(nearest.Addrs.Select(a => Multiaddress.Decode(a.ToByteArray())), Is.EqualTo(new[] { identifiedAddress }));

        ILocalPeer pubsubPeer = Substitute.For<ILocalPeer>();
        pubsubPeer.Identity.Returns(peer.Identity);
        pubsubPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        ISession pubsubSession = Substitute.For<ISession>();
        pubsubSession.RemoteAddress.Returns(identifiedAddress);
        pubsubSession.RemotePeerId.Returns(identified.PeerId);
        pubsubPeer.DialAsync(Arg.Is<Multiaddress[]>(addrs => addrs.SequenceEqual(new[] { identifiedAddress })), Arg.Any<CancellationToken>())
            .Returns(pubsubSession);

        PubsubRouter router = provider.GetRequiredService<PubsubRouter>();
        await router.StartAsync(pubsubPeer);
        await pubsubPeer.Received(1).DialAsync(Arg.Is<Multiaddress[]>(addrs => addrs.SequenceEqual(new[] { identifiedAddress })), Arg.Any<CancellationToken>());
        await pubsubSession.Received(1).DialAsync<GossipsubProtocolV13>(Arg.Any<CancellationToken>());
    }

    private sealed class DhtBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider);

    private sealed class RecordingStore : IPeerStore
    {
        private readonly IPeerStore _store = new PeerStore();

        public PeerId? LastLookup { get; private set; }
        public int Retentions { get; private set; }
        public int Releases { get; private set; }

        public event Action<Multiaddress[]>? OnNewPeer
        {
            add => _store.OnNewPeer += value;
            remove => _store.OnNewPeer -= value;
        }

        public void Discover(Multiaddress[] addrs, bool isTrusted = true) => _store.Discover(addrs, isTrusted);
        public void Discover(ByteString signedPeerRecord, State? state = null) => _store.Discover(signedPeerRecord, state);
        public void SetAgentVersion(PeerId peerId, string? agentVersion) => _store.SetAgentVersion(peerId, agentVersion);
        public void SetSupportedProtocols(PeerId peerId, IReadOnlyList<string> protocols) => _store.SetSupportedProtocols(peerId, protocols);

        public bool TryGetPeerInfo(PeerId peerId, [NotNullWhen(true)] out PeerMetadata? metadata)
        {
            LastLookup = peerId;
            return _store.TryGetPeerInfo(peerId, out metadata);
        }

        public void Retain(PeerId peerId)
        {
            _store.Retain(peerId);
            Retentions++;
        }

        public void Release(PeerId peerId)
        {
            _store.Release(peerId);
            Releases++;
        }
    }
}
