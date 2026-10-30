// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class ProviderResponseBudgetTests
{
    [Test]
    public async Task RegisteredReplyKeepsProvidersWithAddressRichCloserPeersAtDefaultLimits()
    {
        var options = new KadDhtOptions();
        var closer = Enumerable.Range(1, options.KSize).Select(i => Node(i, options.MaxAddressesPerPeer)).ToArray();
        var providers = Enumerable.Range(100, 3).Select(i => Node(i, 2)).ToArray();
        Assert.That(RetainedBytes(closer), Is.GreaterThan(options.MaxPeerResponseBytes));
        var store = Substitute.For<IProviderStore>();
        store.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(providers.Select(node => new ProviderRecord { PeerId = node.PeerId, Multiaddrs = node.Multiaddrs }).ToArray());
        using var harness = new KadWireHarness("direct", options, providers: store, nearest: _ => closer);

        var response = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));

        AssertProviders(response.ProviderPeers, providers);
        Assert.That(response.CloserPeers, Is.Not.Empty);
        Assert.That(response.CloserPeers.Count, Is.LessThanOrEqualTo(options.KSize));
        Assert.That(response.CloserPeers.Sum(peer => peer.Addrs.Count), Is.LessThan(closer.Sum(node => node.Multiaddrs.Count)),
            "Fallback addresses must yield when the shared retained budget is exhausted.");
        AssertBudgets(response, options);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredSendersKeepProvidersWithAddressRichCloserPeersAtDefaultLimits(bool integration)
    {
        var options = new KadDhtOptions();
        var closer = Enumerable.Range(1, options.KSize).Select(i => Node(i, options.MaxAddressesPerPeer)).ToArray();
        var providers = Enumerable.Range(100, 3).Select(i => Node(i, 2)).ToArray();
        Assert.That(RetainedBytes(closer), Is.GreaterThan(options.MaxPeerResponseBytes));
        var response = new Message { Type = Message.Types.MessageType.GetProviders };
        // Other implementations can omit the destination suffix; the reader adds it before retention.
        response.CloserPeers.Add(closer.Select(node => MessageHelper.ToWirePeer(node.PeerId.ToDhtNode(
            node.Multiaddrs.Select(address => address[..address.LastIndexOf("/p2p/", StringComparison.Ordinal)]).ToArray()))));
        response.ProviderPeers.Add(providers.Select(node => MessageHelper.ToWirePeer(node)));
        using var fixture = new SenderFixture(options, response, integration);

        var result = await fixture.Sender.GetProvidersAsync(WirePeerSafetyTests.Id(-1).ToDhtNode(), [1]);

        Assert.That(result.Providers.Select(node => node.PeerId), Is.EqualTo(providers.Select(node => node.PeerId)));
        Assert.That(result.Providers.Select(node => node.Multiaddrs), Is.EqualTo(providers.Select(node => node.Multiaddrs)));
        foreach (var provider in providers)
        {
            Assert.That(fixture.Store.TryGetPeerInfo(provider.PeerId, out var info), Is.True);
            Assert.That(info!.Addrs!.Select(address => address.ToString()), Is.EquivalentTo(provider.Multiaddrs));
        }
        Assert.That(result.CloserPeers, Is.Not.Empty);
        Assert.That(result.CloserPeers.Sum(node => node.Multiaddrs.Count), Is.LessThan(closer.Sum(node => node.Multiaddrs.Count)));
        Assert.That(RetainedBytes(result.Providers.Concat(result.CloserPeers)), Is.LessThanOrEqualTo(options.MaxPeerResponseBytes));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ProviderWithoutAddressesDoesNotSuppressDiscoveryOfUsableCloserPeer(bool integration)
    {
        var node = Node(1, 1);
        var response = new Message
        {
            Type = Message.Types.MessageType.GetProviders,
            ProviderPeers = { MessageHelper.ToWirePeer(node.PeerId.ToDhtNode()) },
            CloserPeers = { MessageHelper.ToWirePeer(node) }
        };
        using var fixture = new SenderFixture(new KadDhtOptions(), response, integration);

        var result = await fixture.Sender.GetProvidersAsync(WirePeerSafetyTests.Id(-1).ToDhtNode(), [1]);

        Assert.That(result.Providers.Single().PeerId, Is.EqualTo(node.PeerId));
        Assert.That(result.Providers.Single().Multiaddrs, Is.Empty);
        Assert.That(result.CloserPeers.Single().Multiaddrs, Is.EqualTo(node.Multiaddrs));
        Assert.That(fixture.Store.TryGetPeerInfo(node.PeerId, out var info), Is.True);
        Assert.That(info!.Addrs!.Select(address => address.ToString()), Is.EquivalentTo(node.Multiaddrs));
    }

    [TestCase(0, 2)]
    [TestCase(-1, 1)]
    public void ProviderPriorityStillHonorsMessageEnvelope(int delta, int addressCount)
    {
        var provider = Node(100, 2);
        var expected = new Message
        {
            Type = Message.Types.MessageType.GetProviders,
            ProviderPeers = { MessageHelper.ToWirePeer(provider) }
        };
        var options = new KadDhtOptions { MaxMessageSize = expected.CalculateSize() + delta };

        var response = MessageHelper.CreatePeerResponse(Message.Types.MessageType.GetProviders, options, [Node(1, 1)], [provider]);

        Assert.That(response.ProviderPeers.Single().Id, Is.EqualTo(expected.ProviderPeers.Single().Id));
        Assert.That(response.ProviderPeers.Single().Addrs, Is.EqualTo(expected.ProviderPeers.Single().Addrs.Take(addressCount)));
        if (delta == 0) Assert.That(response, Is.EqualTo(expected));
        AssertBudgets(response, options);
    }

    private static long RetainedBytes(IEnumerable<DhtNode> nodes) => nodes
        .Sum(node => 2L * node.PeerId.Bytes.Length + node.Multiaddrs.Sum(address => 2L * address.Length));

    private static DhtNode Node(int index, int addressCount)
    {
        var id = new Identity(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index))).PeerId;
        string[] hosts = ["/ip4/203.123.113.201", "/ip4/203.123.113.202", "/ip6/2001:db81:85a3:1319:8a2e:1370:7334:1234", "/ip6/2001:db81:85a3:1319:8a2e:1370:7334:1235"];
        string[] transports = ["/tcp/4001", "/udp/4001/quic-v1", "/tcp/443/ws", "/udp/4001/webrtc-direct"];
        return id.ToDhtNode(hosts.SelectMany(host => transports.Select(transport => $"{host}{transport}/p2p/{id}"))
            .Take(addressCount).Select(address => Multiaddress.Decode(address).ToString()).ToArray());
    }

    private static void AssertProviders(IEnumerable<Message.Types.Peer> actual, DhtNode[] expected)
    {
        Assert.That(actual.Select(peer => new PeerId(peer.Id.ToByteArray())), Is.EqualTo(expected.Select(node => node.PeerId)));
        Assert.That(actual.Select(peer => peer.Addrs.Select(address => Multiaddress.Decode(address.ToByteArray()).ToString())),
            Is.EqualTo(expected.Select(node => node.Multiaddrs)));
    }

    private static void AssertBudgets(Message response, KadDhtOptions options)
    {
        Assert.That(response.CalculateSize(), Is.LessThanOrEqualTo(options.MaxMessageSize));
        // The message consists only of its type and the two peer lists.
        Assert.That(response.CalculateSize() - new Message { Type = response.Type }.CalculateSize(),
            Is.LessThanOrEqualTo(options.MaxPeerResponseBytes));
        long retained = response.ProviderPeers.Concat(response.CloserPeers).Sum(peer => 2L * peer.Id.Length +
            peer.Addrs.Sum(address => 2L * Multiaddress.Decode(address.ToByteArray()).ToString().Length));
        Assert.That(retained, Is.LessThanOrEqualTo(options.MaxPeerResponseBytes));
    }
}
