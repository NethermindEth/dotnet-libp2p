// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Libp2p.Protocols.KadDht.Tests.Integration.WirePeerSafetyTests;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class WirePeerBudgetTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task BothSenderFactories_UseConfiguredCountsAndProviderAddressLimit(bool integration)
    {
        var options = new KadDhtOptions { KSize = 2, MaxProvidersPerKey = 2, MaxAddressesPerPeer = 1, MaxAddressesPerProvider = 17 };
        var response = new Message();
        for (int i = 0; i < 5; i++) response.CloserPeers.Add(WirePeer(i, "/ip4/1.1.1.1", "/ip4/2.2.2.2"));
        for (int i = 0; i < 5; i++) response.ProviderPeers.Add(WirePeer(i + 10,
            Enumerable.Range(1, 18).Select(n => $"/ip4/127.0.0.{n}").ToArray()));
        using var fixture = new SenderFixture(options, response, integration);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers.Select(p => p.PeerId), Is.EqualTo(new[] { Id(0), Id(1) }));
        Assert.That(result.CloserPeers.Select(p => p.Multiaddrs.Single()),
            Is.EqualTo(new[] { $"/ip4/1.1.1.1/p2p/{Id(0)}", $"/ip4/1.1.1.1/p2p/{Id(1)}" }));
        Assert.That(result.Providers.Select(p => p.PeerId), Is.EqualTo(new[] { Id(10), Id(11) }));
        Assert.That(result.Providers[0].Multiaddrs, Is.EqualTo(Enumerable.Range(1, 17).Select(n => $"/ip4/127.0.0.{n}/p2p/{Id(10)}")));
        Assert.That(fixture.Store.TryGetPeerInfo(Id(12), out _), Is.False);
    }

    [Test]
    public async Task PeersWithoutAddressesCloserPeersAreDropped_ProvidersAndStandaloneConversionAllowThem()
    {
        var response = new Message();
        response.CloserPeers.Add(WirePeer(1));
        response.ProviderPeers.Add(WirePeer(2));
        using var fixture = new SenderFixture(new KadDhtOptions(), response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers, Is.Empty);
        Assert.That(result.Providers.Single().PeerId, Is.EqualTo(Id(2)));
        Assert.That(result.Providers.Single().Multiaddrs, Is.Empty);
        Assert.That(MessageHelper.FromWirePeer(response.CloserPeers[0])!.Multiaddrs, Is.Empty);
        Assert.That(fixture.Store.TryGetPeerInfo(Id(1), out _), Is.False);
        Assert.That(fixture.Store.TryGetPeerInfo(Id(2), out _), Is.False);
    }

    [Test]
    public async Task InvalidDuplicateAndSkippedEntriesConsumePrefixSlots()
    {
        var response = new Message();
        var peer = WirePeer(1, "/ip4/1.1.1.1");
        response.CloserPeers.Add(new Message.Types.Peer { Id = ByteString.CopyFrom(new byte[] { 255, 255, 255 }) });
        response.CloserPeers.Add(peer);
        response.CloserPeers.Add(peer.Clone());
        response.CloserPeers.Add(WirePeer(2, "/ip4/2.2.2.2"));
        response.ProviderPeers.Add(peer.Clone());
        using var fixture = new SenderFixture(new KadDhtOptions { KSize = 3 }, response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers.Select(p => p.PeerId), Is.EqualTo(new[] { Id(1) }));
        Assert.That(result.Providers.Select(p => p.PeerId), Is.EqualTo(new[] { Id(1) }));
        Assert.That(fixture.Store.TryGetPeerInfo(Id(2), out _), Is.False);
        var discovered = new List<PeerId>();
        var sender = new LibP2pKademliaMessageSender(new KadDhtOptions { KSize = 3 }, fixture.Local,
            onPeerDiscovered: node => discovered.Add(node.PeerId));
        await sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(discovered, Is.EqualTo(new[] { Id(1) }));
    }

    [TestCase(0, 1)]
    [TestCase(-1, 0)]
    public async Task RetainedBudgetIncludesBothIdentityCopiesAndNormalizedUtf16(int delta, int expected)
    {
        string normalized = $"/ip4/1.1.1.1/p2p/{Id(1)}";
        int size = 2 * Id(1).Bytes.Length + 2 * normalized.Length;
        var response = new Message();
        response.CloserPeers.Add(WirePeer(1, "/ip4/1.1.1.1"));
        using var fixture = new SenderFixture(new KadDhtOptions { MaxPeerRecordBytes = size + delta }, response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers, Has.Length.EqualTo(expected));
        if (expected == 1) Assert.That(result.CloserPeers[0].Multiaddrs, Is.EqualTo(new[] { normalized }));
    }

    [TestCase(0, 1)]
    [TestCase(-1, 0)]
    public async Task CombinedBudgetIsShared_ProvidersHavePriority(int delta, int expectedCloser)
    {
        string normalized = $"/ip4/1.1.1.1/p2p/{Id(1)}";
        string providerAddress = $"/ip4/2.2.2.2/p2p/{Id(2)}";
        int closerBytes = 2 * Id(1).Bytes.Length + 2 * normalized.Length;
        int providerBytes = 2 * Id(2).Bytes.Length + 2 * providerAddress.Length;
        var response = new Message();
        response.CloserPeers.Add(WirePeer(1, "/ip4/1.1.1.1"));
        response.ProviderPeers.Add(WirePeer(2, "/ip4/2.2.2.2"));
        using var fixture = new SenderFixture(new KadDhtOptions { MaxPeerResponseBytes = closerBytes + providerBytes + delta }, response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.Providers.Single().PeerId, Is.EqualTo(Id(2)));
        Assert.That(result.Providers.Single().Multiaddrs, Is.EqualTo(new[] { providerAddress }));
        Assert.That(result.CloserPeers, Has.Length.EqualTo(expectedCloser));
        if (expectedCloser == 1) Assert.That(result.CloserPeers[0].Multiaddrs, Is.EqualTo(new[] { normalized }));
    }

    [Test]
    public async Task OversizedAndMalformedAddressesCannotBypassWorkOrRetentionBudgets()
    {
        var response = new Message();
        var huge = WirePeer(1, "/unix/" + new string('x', 100_000), "/ip6/::1/tcp/4001", "/dns/example.org/tcp/4001");
        response.CloserPeers.Add(huge);
        var malformed = WirePeer(2);
        for (int i = 0; i < 100; i++) malformed.Addrs.Add(ByteString.CopyFrom(new byte[] { 4 }));
        malformed.Addrs.Add(ByteString.CopyFrom(Multiaddress.Decode("/ip4/1.1.1.1").ToBytes()));
        response.CloserPeers.Add(malformed);
        using var fixture = new SenderFixture(new KadDhtOptions { MaxAddressesPerPeer = 101, MaxPeerRecordBytes = 300 }, response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers.Single().PeerId, Is.EqualTo(Id(1)));
        Assert.That(result.CloserPeers[0].Multiaddrs, Is.EqualTo(new[] { $"/ip6/::1/tcp/4001/p2p/{Id(1)}" }));
        Assert.That(fixture.Store.TryGetPeerInfo(Id(2), out _), Is.False);
    }

    [Test]
    public async Task DuplicateAndMalformedAddressesConsumeSlots_AddressesAreDeduplicated()
    {
        var peer = WirePeer(1, "/ip4/1.1.1.1", "/ip4/1.1.1.1", "/ip4/2.2.2.2");
        peer.Addrs.Insert(0, ByteString.CopyFrom(new byte[] { 4 }));
        var response = new Message();
        response.CloserPeers.Add(peer);
        using var fixture = new SenderFixture(new KadDhtOptions { MaxAddressesPerPeer = 3 }, response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers.Single().Multiaddrs, Is.EqualTo(new[] { $"/ip4/1.1.1.1/p2p/{Id(1)}" }));
    }

    // Varint vectors include TestBadVarint from go-multihash at revision
    // e5d5390ef42395d99ae7c0ca072672b6f723da06 (multihash_test.go).
    [TestCase("FFFFFF")]
    [TestCase("818080")]
    [TestCase("818080808080808080808101")]
    [TestCase("8100")]
    [TestCase("8000")]
    [TestCase("12810000")]
    [TestCase("FFFFFFFFFFFFFFFFFF0100")]
    public async Task InvalidIdsNeverReachProviderStorageOrDiscovery(string hex)
    {
        var peer = WirePeer(1, "/ip4/1.1.1.1");
        peer.Id = ByteString.CopyFrom(Convert.FromHexString(hex));
        var response = new Message();
        response.CloserPeers.Add(peer);
        response.ProviderPeers.Add(peer);
        using var fixture = new SenderFixture(new KadDhtOptions(), response);
        var result = await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1]);
        Assert.That(result.CloserPeers, Is.Empty);
        Assert.That(result.Providers, Is.Empty);
        var store = Substitute.For<IProviderStore>();
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), providers: store);
        await harness.SendAsync(new Message { Type = Message.Types.MessageType.AddProvider, ProviderPeers = { peer } });
        await store.DidNotReceive().AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ProviderRecord>(), Arg.Any<CancellationToken>());
    }

    [TestCase(33, 0)]
    [TestCase(34, 1)]
    public async Task ConfiguredIdentityByteLimitIsAppliedBeforeConversion(int limit, int count)
    {
        var response = new Message();
        response.ProviderPeers.Add(WirePeer(1));
        using var fixture = new SenderFixture(new KadDhtOptions { MaxPeerIdBytes = limit }, response);
        Assert.That((await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1])).Providers, Has.Length.EqualTo(count));
        var identity = new Identity(new byte[32]).PeerId;
        Assert.That(MessageHelper.FromWirePeer(new Message.Types.Peer { Id = ByteString.CopyFrom(identity.Bytes) })!.PeerId, Is.EqualTo(identity));
        Assert.That(MessageHelper.FromWirePeer(WirePeer(1))!.PeerId, Is.EqualTo(Id(1)));
        // Structural validation does not impose a new hash algorithm allowlist.
        byte[] unfamiliar = [0x99, 0x01, 0x01, 0x42];
        Assert.That(MessageHelper.FromWirePeer(new Message.Types.Peer { Id = ByteString.CopyFrom(unfamiliar) })!.PeerId.Bytes, Is.EqualTo(unfamiliar));
    }

    [TestCase("services")]
    [TestCase("fallback")]
    [TestCase("integration")]
    [TestCase("direct")]
    public async Task ProviderRepliesFromCustomStoreUseResponseLimits(string path)
    {
        var store = Substitute.For<IProviderStore>();
        store.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 20).Select(i => new ProviderRecord
            {
                PeerId = Id(i),
                Multiaddrs = ["/unix/" + new string('x', 10_000), "/ip4/1.1.1.1"],
                Ttl = TimeSpan.FromHours(1)
            }).ToArray());
        using var harness = new KadWireHarness(path, new KadDhtOptions { MaxProvidersPerKey = 2, MaxPeerRecordBytes = 200 }, providers: store);
        var result = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));
        Assert.That(result.ProviderPeers.Select(p => new PeerId(p.Id.ToByteArray())), Is.EqualTo(new[] { Id(1), Id(2) }));
        Assert.That(result.ProviderPeers.Select(p => Multiaddress.Decode(p.Addrs.Single().ToByteArray()).ToString()),
            Is.EqualTo(new[] { "/ip4/1.1.1.1", "/ip4/1.1.1.1" }));
    }

    [TestCase(0, 1)]
    [TestCase(-1, 0)]
    public async Task RegisteredRepliesShareRetainedBudgetAcrossBothLists(int delta, int expectedCloserAddresses)
    {
        var store = Substitute.For<IProviderStore>();
        store.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderRecord[] { new() { PeerId = Id(2), Multiaddrs = ["/ip4/2.2.2.2"] } });
        int budget = 2 * Id(1).Bytes.Length + 2 * Id(2).Bytes.Length +
            2 * "/ip4/1.1.1.1".Length + 2 * "/ip4/2.2.2.2".Length;
        using var harness = new KadWireHarness("direct", new KadDhtOptions { MaxPeerResponseBytes = budget + delta },
            providers: store, nearest: _ => [Id(1).ToDhtNode(["/ip4/1.1.1.1"])]);
        var response = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));
        Assert.That(response.CloserPeers.Single().Id, Is.EqualTo(ByteString.CopyFrom(Id(1).Bytes)));
        Assert.That(response.CloserPeers[0].Addrs, Has.Count.EqualTo(expectedCloserAddresses));
        if (expectedCloserAddresses == 1)
            Assert.That(Multiaddress.Decode(response.CloserPeers[0].Addrs.Single().ToByteArray()).ToString(), Is.EqualTo("/ip4/1.1.1.1"));
        Assert.That(response.ProviderPeers.Single().Id, Is.EqualTo(ByteString.CopyFrom(Id(2).Bytes)));
        Assert.That(Multiaddress.Decode(response.ProviderPeers[0].Addrs.Single().ToByteArray()).ToString(), Is.EqualTo("/ip4/2.2.2.2"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task OutboundPeerFramingFitsMessageEnvelopeExactly(int delta)
    {
        var raw = WirePeer(1, "/ip4/1.1.1.1");
        raw.Connection = Message.Types.ConnectionType.NotConnected;
        var expected = new Message { Type = Message.Types.MessageType.FindNode, CloserPeers = { raw } };
        using var harness = new KadWireHarness("direct", new KadDhtOptions { MaxMessageSize = expected.CalculateSize() + delta },
            nearest: _ => [Id(1).ToDhtNode(["/ip4/1.1.1.1"])]);
        var response = await harness.SendAsync(MessageHelper.CreateFindNodeRequest([1]));
        Assert.That(response.CalculateSize(), Is.LessThanOrEqualTo(expected.CalculateSize() + delta));
        Assert.That(response.CloserPeers.Single().Addrs.Count, Is.EqualTo(delta == 0 ? 1 : 0));
    }
}

internal sealed class SenderFixture : IDisposable
{
    private readonly ServiceProvider _provider;
    internal ILocalPeer Local { get; } = Substitute.For<ILocalPeer>();
    internal PeerStore Store { get; } = new();
    internal IDhtMessageSender Sender { get; }

    internal SenderFixture(KadDhtOptions options, Message response, bool integration = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Local);
        services.AddSingleton(Store);
        if (integration)
        {
            using var initial = services.BuildServiceProvider();
            var builder = Substitute.For<ILibp2pPeerFactoryBuilder>();
            builder.Services.Returns(services);
            builder.ServiceProvider.Returns(initial);
            builder.AddProtocol(Arg.Any<RequestResponseProtocol<Message, Message>>(), Arg.Any<bool>()).Returns(builder);
            builder.AddProtocol<KadDhtProtocol>().Returns(builder);
            builder.AddKadDht(o => CopyOptions(options, o));
        }
        else services.AddKadDht(o => CopyOptions(options, o));
        _provider = services.BuildServiceProvider();
        var session = Substitute.For<ISession>();
        Local.DialAsync(Id(-1), Arg.Any<CancellationToken>()).Returns(session);
        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(response);
        Sender = _provider.GetRequiredService<IDhtMessageSender>();
    }

    private static void CopyOptions(KadDhtOptions source, KadDhtOptions target)
    {
        foreach (var property in typeof(KadDhtOptions).GetProperties().Where(p => p.CanWrite))
            property.SetValue(target, property.GetValue(source));
    }

    public void Dispose() => _provider.Dispose();
}
