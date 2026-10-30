// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class ProviderRetentionIntegrationTests
{
    [TestCase("services")]
    [TestCase("fallback")]
    [TestCase("integration")]
    [TestCase("direct")]
    public async Task DefaultStoreConstruction_UsesAllConfiguredLimits(string path)
    {
        var options = new KadDhtOptions
        {
            MaxProviderKeys = 2,
            MaxProvidersPerKey = 1,
            MaxAddressesPerProvider = 17,
            MaxProviderRecordBytes = 2048
        };
        using var harness = new WireHarness(path, options);
        var first = new Identity(new byte[32]).PeerId;
        var second = new Identity(Enumerable.Repeat((byte)1, 32).ToArray()).PeerId;
        var addresses = new[] { "/unix/" + new string('x', 600) }
            .Concat(Enumerable.Repeat("/ip4/127.0.0.1", 16)).Append("/ip4/1.1.1.1").ToArray();
        await harness.AnnounceAsync(Announcement([1], first, addresses), first);
        await harness.AnnounceAsync(Announcement([1], second), second);
        await harness.AnnounceAsync(Announcement([2], first), first);
        await harness.AnnounceAsync(Announcement([3], first), first);
        var providers = (await harness.QueryAsync([1])).ProviderPeers;
        Assert.That(providers, Has.Count.EqualTo(1));
        Assert.That(providers.Single().Id, Is.EqualTo(ByteString.CopyFrom(first.Bytes)));
        Assert.That(providers.Single().Addrs.Select(a => Multiaddress.Decode(a.ToByteArray()).ToString()),
            Is.EqualTo(addresses.Take(17)));
        Assert.That((await harness.QueryAsync([2])).ProviderPeers, Has.Count.EqualTo(1));
        Assert.That((await harness.QueryAsync([3])).ProviderPeers, Is.Empty);
    }

    [TestCase("services")]
    [TestCase("fallback")]
    [TestCase("integration")]
    [TestCase("direct")]
    public async Task CustomStore_IsPreservedAndReceivesBoundedPayload(string path)
    {
        var store = Substitute.For<IProviderStore>();
        store.AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ProviderRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var options = new KadDhtOptions { MaxAddressesPerProvider = 2, MaxProviderRecordBytes = 200 };
        using var harness = new WireHarness(path, options, store);
        var peer = new Identity(new byte[32]).PeerId;
        var message = Announcement([1], peer, "/unix/" + new string('x', 200), "/ip4/1.1.1.1", "/ip4/2.2.2.2");
        await harness.AnnounceAsync(message, peer);
        await store.Received(1).AddProviderAsync(Arg.Is<ReadOnlyMemory<byte>>(key => key.Length == 1),
            Arg.Is<ProviderRecord>(p => p.PeerId.Equals(peer) && p.Multiaddrs.SequenceEqual(new[] { "/ip4/1.1.1.1" })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LargeUnixAddress_ParsesButIsOmittedBeforeCustomStore()
    {
        // unix code 400 and a 1,000,000-byte component length, both unsigned varint values.
        byte[] encoded = new byte[1_000_005];
        new byte[] { 0x90, 0x03, 0xc0, 0x84, 0x3d }.CopyTo(encoded, 0);
        Array.Fill(encoded, (byte)'x', 5, 1_000_000);
        string decoded = Multiaddress.Decode(encoded).ToString();
        Assert.That(decoded.Length, Is.EqualTo(1_000_006));
        Assert.That(decoded.StartsWith("/unix/", StringComparison.Ordinal), Is.True);
        var store = Substitute.For<IProviderStore>();
        using var harness = new WireHarness("direct", new KadDhtOptions(), store);
        var peer = new Identity(new byte[32]).PeerId;
        var message = Announcement([1], peer);
        message.ProviderPeers.Single().Addrs.Add(ByteString.CopyFrom(encoded));
        Assert.That(message.CalculateSize(), Is.LessThan(new KadDhtOptions().MaxMessageSize));
        await harness.AnnounceAsync(message, peer);
        await store.Received(1).AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Is<ProviderRecord>(p => p.PeerId.Equals(peer) && p.Multiaddrs.Count == 0), Arg.Any<CancellationToken>());
    }

    [TestCase(0, 1)]
    [TestCase(-1, 0)]
    public async Task WireBudget_AccountsForNormalizedStringRatherThanEncodedLength(int delta, int expectedCount)
    {
        var peer = new Identity(new byte[32]).PeerId;
        const string address = "/ip4/127.0.0.1";
        int identityBytes = 8 + peer.Bytes.Length + 2 * peer.ToString().Length;
        var options = new KadDhtOptions { MaxProviderRecordBytes = identityBytes + 2 * address.Length + delta };
        var store = Substitute.For<IProviderStore>();
        using var harness = new WireHarness("direct", options, store);
        await harness.AnnounceAsync(Announcement([1], peer, address), peer);
        await store.Received(1).AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Is<ProviderRecord>(p => p.Multiaddrs.Count == expectedCount &&
                (expectedCount == 0 || p.Multiaddrs[0] == address)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WireAddresses_MalformedEntryConsumesInspectionSlot()
    {
        var peer = new Identity(new byte[32]).PeerId;
        var store = Substitute.For<IProviderStore>();
        using var harness = new WireHarness("direct", new KadDhtOptions { MaxAddressesPerProvider = 2 }, store);
        var message = Announcement([1], peer, "/ip4/1.1.1.1", "/ip4/2.2.2.2");
        // Recognized ip4 protocol with a truncated payload.
        message.ProviderPeers.Single().Addrs.Insert(0, ByteString.CopyFrom(new byte[] { 4 }));
        await harness.AnnounceAsync(message, peer);
        await store.Received(1).AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Is<ProviderRecord>(p => p.Multiaddrs.SequenceEqual(new[] { "/ip4/1.1.1.1" })), Arg.Any<CancellationToken>());
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public async Task Handler_PreservesSenderAndServerModeGates(bool client, bool mismatch)
    {
        var peer = new Identity(new byte[32]).PeerId;
        var other = new Identity(Enumerable.Repeat((byte)1, 32).ToArray()).PeerId;
        var store = Substitute.For<IProviderStore>();
        var options = new KadDhtOptions { Mode = client ? KadDhtMode.Client : KadDhtMode.Server };
        using var harness = new WireHarness("direct", options, store);
        await harness.AnnounceAsync(Announcement([1], peer), mismatch ? other : peer);
        await store.Received(client || mismatch ? 0 : 1).AddProviderAsync(
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ProviderRecord>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WireBudget_RejectsOversizedKeyOrPeerBeforeCustomStore(bool oversizedPeer)
    {
        var peer = new Identity(new byte[32]).PeerId;
        var store = Substitute.For<IProviderStore>();
        using var harness = new WireHarness("direct", new KadDhtOptions(), store);
        var message = Announcement(oversizedPeer ? new byte[] { 1 } : new byte[100_000], peer);
        if (oversizedPeer) message.ProviderPeers.Single().Id = ByteString.CopyFrom(new byte[100_000]);
        await harness.AnnounceAsync(message, peer);
        await store.DidNotReceive().AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ProviderRecord>(), Arg.Any<CancellationToken>());
    }

    [TestCase("keys")]
    [TestCase("providers")]
    [TestCase("addresses")]
    [TestCase("bytes")]
    public void Registration_RejectsInvalidLimitsEvenWithCustomStore(string limit)
    {
        var options = new KadDhtOptions();
        switch (limit)
        {
            case "keys": options.MaxProviderKeys = 0; break;
            case "providers": options.MaxProvidersPerKey = 0; break;
            case "addresses": options.MaxAddressesPerProvider = 0; break;
            case "bytes": options.MaxProviderRecordBytes = 0; break;
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new WireHarness("direct", options, Substitute.For<IProviderStore>()));
    }

    private static Message Announcement(byte[] key, PeerId peer, params string[] addresses) =>
        MessageHelper.CreateAddProviderRequest(key, [peer.ToDhtNode(addresses)]);

    private sealed class WireHarness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly RequestResponseProtocol<Message, Message> _protocol;

        internal WireHarness(string path, KadDhtOptions options, IProviderStore? store = null)
        {
            var services = new ServiceCollection();
            if (path == "services") services.AddKadDht(o => CopyOptions(options, o));
            else services.AddSingleton(options);
            if (store is not null) services.AddSingleton(store);
            _services = services.BuildServiceProvider();
            var builder = Substitute.For<ILibp2pPeerFactoryBuilder>();
            builder.Services.Returns(services);
            builder.ServiceProvider.Returns(_services);
            RequestResponseProtocol<Message, Message>? protocol = null;
            builder.AddProtocol(Arg.Any<RequestResponseProtocol<Message, Message>>(), Arg.Any<bool>())
                .Returns(call => { protocol = call.Arg<RequestResponseProtocol<Message, Message>>(); return builder; });
            builder.AddProtocol<KadDhtProtocol>().Returns(builder);
            try
            {
                switch (path)
                {
                    case "services":
                    case "fallback": builder.WithKadDht(); break;
                    case "integration": builder.AddKadDht(o => CopyOptions(options, o), providerStore: store); break;
                    case "direct": builder.AddKadDhtProtocols(_ => [], options: options, providerStore: store); break;
                    default: throw new ArgumentException("Unknown registration path", nameof(path));
                }
                _protocol = protocol!;
            }
            catch
            {
                _services.Dispose();
                throw;
            }
        }

        internal async Task AnnounceAsync(Message message, PeerId sender)
        {
            var context = Context(sender);
            var channel = new Channel();
            var listen = _protocol.ListenAsync(channel.Reverse, context);
            try
            {
                var read = channel.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask();
                await ((IChannel)channel).WriteSizeAndProtobufAsync(message);
                var response = await read.WaitAsync(TimeSpan.FromSeconds(5));
                await listen.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(response.Result, Is.EqualTo(IOResult.Ended));
            }
            finally
            {
                await channel.CloseAsync();
                await listen.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        internal async Task<Message> QueryAsync(byte[] key)
        {
            var context = Context(new Identity(new byte[32]).PeerId);
            var channel = new Channel();
            var listen = _protocol.ListenAsync(channel.Reverse, context);
            try
            {
                return await _protocol.DialAsync(channel, context, new Message
                {
                    Type = Message.Types.MessageType.GetProviders,
                    Key = ByteString.CopyFrom(key)
                }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await channel.CloseAsync();
                await listen.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static ISessionContext Context(PeerId peer)
        {
            var context = Substitute.For<ISessionContext>();
            context.State.Returns(new State { RemoteAddress = Multiaddress.Decode($"/p2p/{peer}") });
            return context;
        }

        private static void CopyOptions(KadDhtOptions source, KadDhtOptions target)
        {
            target.Mode = source.Mode;
            target.MaxProviderKeys = source.MaxProviderKeys;
            target.MaxProvidersPerKey = source.MaxProvidersPerKey;
            target.MaxAddressesPerProvider = source.MaxAddressesPerProvider;
            target.MaxProviderRecordBytes = source.MaxProviderRecordBytes;
        }

        public void Dispose() => _services.Dispose();
    }
}
