// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class IpColocationTests
{
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("10.0.0.0/33")]
    [TestCase("2001:db8::/129")]
    [TestCase("10.0.0.0/-1")]
    public void InvalidCidrDoesNotModifyTheWhitelist(string cidr)
    {
        PubsubSettings settings = new();
        settings.AddIPColocationWhitelist("10.0.0.0/8");

        Assert.Throws<FormatException>(() => settings.AddIPColocationWhitelist(cidr));

        Assert.That(settings.IPColocationFactorWhitelist, Has.Count.EqualTo(1));
        Assert.That(IpColocation.GetKey(Address(1, "/ip4/10.0.0.1/tcp/4001"), settings.IPColocationFactorWhitelist), Is.Null);
    }

    [TestCase("/ip4/192.0.2.1/tcp/4001", "192.0.2.1")]
    [TestCase("/ip6/::ffff:192.0.2.1/tcp/4001", "192.0.2.1")]
    [TestCase("/ip6/2001:db8:abcd:12::1/udp/4001/quic-v1", "2001:db8:abcd:12::/64")]
    [TestCase("/ip6/2001:0DB8:ABCD:0012:0000:0000:0000:0002/tcp/4002", "2001:db8:abcd:12::/64")]
    [TestCase("/ip4/10.0.0.1/tcp/4001", "10.0.0.1")]
    [TestCase("/ip4/172.16.0.1/tcp/4001", "172.16.0.1")]
    [TestCase("/ip4/192.168.1.1/tcp/4001", "192.168.1.1")]
    [TestCase("/ip6/fd12:3456:789a:1::1/tcp/4001", "fd12:3456:789a:1::/64")]
    [TestCase("/ip4/127.0.0.1/tcp/4001", null)]
    [TestCase("/ip4/127.2.3.4/tcp/4001", null)]
    [TestCase("/ip6/::1/tcp/4001", null)]
    [TestCase("/ip6/::ffff:127.0.0.1/tcp/4001", null)]
    [TestCase("/ip4/0.0.0.0/tcp/4001", null)]
    [TestCase("/ip6/::/tcp/4001", null)]
    [TestCase("/ip6/::ffff:0.0.0.0/tcp/4001", null)]
    [TestCase("/ip6/fe80::1%3/tcp/4001", null)]
    [TestCase("/dns/example.org/tcp/4001", null)]
    [TestCase("/dns4/example.org/tcp/4001", null)]
    [TestCase("/dns6/example.org/tcp/4001", null)]
    [TestCase("/dnsaddr/example.org", null)]
    [TestCase("", null)]
    public void KeysNormalizeLiteralIpsAndExcludeUnusableAddresses(string endpoint, string? expected)
    {
        Assert.That(IpColocation.GetKey(Address(1, endpoint), []), Is.EqualTo(expected));
    }

    [Test]
    public void RelayTransportAndDestinationIpsAreNeverUsed()
    {
        Multiaddress relay = Address(99, "/ip4/192.0.2.1/tcp/4001");
        Assert.That(IpColocation.GetKey(Address(1, $"{relay}/p2p-circuit"), []), Is.Null);
        Assert.That(IpColocation.GetKey(Address(1, $"{relay}/p2p-circuit/ip4/198.51.100.1/tcp/4001"), []), Is.Null);
    }

    [TestCase("/ip4/192.0.2.1/tcp/4001", "192.0.2.0/24")]
    [TestCase("/ip6/::ffff:192.0.2.1/tcp/4001", "192.0.2.0/24")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "2001:db8::/32")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "2001:db8:1::1/128")]
    public void WhitelistChecksTheNormalizedAddressBeforeGrouping(string endpoint, string network)
    {
        Assert.That(IpColocation.GetKey(Address(1, endpoint), WhitelistedSettings(network).IPColocationFactorWhitelist), Is.Null);
        Assert.That(IpColocation.GetKey(Address(1, endpoint), WhitelistedSettings("198.51.100.0/24").IPColocationFactorWhitelist), Is.Not.Null);
    }

    [Test]
    public async Task ConnectionsApplyTheSquaredSurplusPenaltyInBothDirections()
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress[] addresses = Enumerable.Range(1, 4).Select(i => Address(i, "/ip4/192.0.2.1/tcp/4001")).ToArray();
        Connect(router, addresses[0]);
        Connect(router, addresses[1], inbound: true);
        AssertScores(router, addresses.Take(2), 0);

        Connect(router, addresses[2]);
        AssertScores(router, addresses.Take(3), -1);

        Connect(router, addresses[3], inbound: true);
        AssertScores(router, addresses, -4);
    }

    [TestCase("/ip4/192.0.2.1/tcp/4001", "/ip6/::ffff:192.0.2.1/tcp/5000")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "/ip6/2001:0DB8:0001:0:0:0:0:2/udp/5000/quic-v1")]
    [TestCase("/ip4/10.0.0.1/tcp/4001", "/ip4/10.0.0.1/tcp/5000")]
    public async Task NormalizedAddressesShareOneBucket(string firstEndpoint, string secondEndpoint)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress[] addresses = [Address(1, firstEndpoint), Address(2, secondEndpoint), Address(3, secondEndpoint)];
        foreach (Multiaddress address in addresses) Connect(router, address);

        AssertScores(router, addresses, -1);
    }

    [TestCase("/ip4/127.0.0.1/tcp/4001")]
    [TestCase("/ip6/::1/tcp/4001")]
    [TestCase("/dns4/example.org/tcp/4001")]
    [TestCase("/dnsaddr/example.org")]
    [TestCase("")]
    [TestCase("/ip4/192.0.2.1/tcp/4001/p2p-circuit")]
    public async Task ExcludedPeersDoNotShareAnAccidentalBucket(string endpoint)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress[] addresses = Enumerable.Range(1, 4).Select(i => Address(i, endpoint)).ToArray();
        foreach (Multiaddress address in addresses) Connect(router, address);

        AssertScores(router, addresses, 0);
        Assert.That(BucketCounts(router), Is.Empty);
    }

    [Test]
    public async Task AdvertisedIpsCannotSupplyTheBucketForAnUnresolvedConnection()
    {
        PeerStore store = new();
        store.Discover(Enumerable.Range(1, 4).Select(i => Address(i, "/ip4/192.0.2.1/tcp/4001")).ToArray());
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            AutoConnect = false,
            IPColocationFactorThreshold = 2,
            IPColocationFactorWeight = -1
        });
        Multiaddress[] connections = Enumerable.Range(1, 4).Select(i => Address(i, "/dns4/example.org/tcp/4001")).ToArray();
        foreach (Multiaddress address in connections) Connect(router, address);

        AssertScores(router, connections, 0);
        Assert.That(BucketCounts(router), Is.Empty);
    }

    [Test]
    public async Task DifferentIpsAndIpv6PrefixesHaveIndependentBuckets()
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress[] addresses =
        [
            Address(1, "/ip4/192.0.2.1/tcp/4001"),
            Address(2, "/ip4/192.0.2.2/tcp/4001"),
            Address(3, "/ip6/2001:db8:1::1/tcp/4001"),
            Address(4, "/ip6/2001:db8:2::1/tcp/4001")
        ];
        foreach (Multiaddress address in addresses) Connect(router, address);

        AssertScores(router, addresses, 0);
        Assert.That(BucketCounts(router).Values, Is.All.EqualTo(1));
    }

    [Test]
    public async Task DefaultSettingsDoNotPenalizeLocalMultiPeerNetworks()
    {
        await using PubsubRouter router = CreateRouter(new PubsubSettings { AutoConnect = false });
        Multiaddress[] addresses = Enumerable.Range(1, 12).Select(i => Address(i, "/ip4/127.0.0.1/tcp/4001")).ToArray();
        foreach (Multiaddress address in addresses) Connect(router, address);

        AssertScores(router, addresses, 0);
    }

    [TestCase("/ip4/10.0.0.1/tcp/4001", "10.0.0.0/8")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "2001:db8::/32")]
    public async Task WhitelistedPeersNeitherCountNorReceiveColocationPenalties(string endpoint, string network)
    {
        await using PubsubRouter router = CreateRouter(WhitelistedSettings(network));
        Multiaddress[] exempt = Enumerable.Range(1, 4).Select(i => Address(i, endpoint)).ToArray();
        Multiaddress[] counted = Enumerable.Range(5, 3).Select(i => Address(i, "/ip4/198.51.100.1/tcp/4001")).ToArray();
        foreach (Multiaddress address in exempt.Concat(counted)) Connect(router, address);

        AssertScores(router, exempt, 0);
        AssertScores(router, counted, -1);
        Assert.That(BucketCounts(router), Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisconnectRemovesOnlyTheDepartingPeerAndDeletesEmptyBuckets(bool inbound)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress[] addresses = Enumerable.Range(1, 3).Select(i => Address(i, "/ip4/192.0.2.1/tcp/4001")).ToArray();
        TaskCompletionSource[] connections = addresses.Select(address => Connect(router, address, inbound)).ToArray();
        AssertScores(router, addresses, -1);

        await Disconnect(router, addresses[2], connections[2]);
        AssertScores(router, addresses.Take(2), 0);
        Assert.That(BucketCounts(router).Values, Is.EquivalentTo(new[] { 2 }));

        await Disconnect(router, addresses[0], connections[0]);
        await Disconnect(router, addresses[1], connections[1]);
        Assert.That(BucketCounts(router), Is.Empty, "Retained scores must not count as connected peers.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AdditionalStreamsKeepTheFirstAddressAndDoNotCountTwice(bool inboundFirst)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress first = Address(1, "/ip4/192.0.2.1/tcp/4001");
        Connect(router, first, inboundFirst);
        Connect(router, Address(1, "/ip4/198.51.100.1/tcp/5000"), !inboundFirst);
        Connect(router, first, inbound: true);
        Multiaddress second = Address(2, "/ip4/192.0.2.1/tcp/5000");
        Connect(router, second);
        AssertScores(router, [first, second], 0);

        Multiaddress third = Address(3, "/ip4/192.0.2.1/tcp/6000");
        Connect(router, third);
        AssertScores(router, [first, second, third], -1);
        Assert.That(BucketCounts(router), Is.EquivalentTo(new Dictionary<string, int> { ["192.0.2.1"] = 3 }));
    }

    [Test]
    public async Task ClosingOneOfSeveralInboundStreamsDoesNotRemoveThePeer()
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress address = Address(1, "/ip4/192.0.2.1/tcp/4001");
        TaskCompletionSource first = Connect(router, address, inbound: true);
        TaskCompletionSource last = Connect(router, address, inbound: true);
        PubsubRouter.PubsubPeer peer = Peer(router, address);
        first.SetResult();
        Assert.That(await Task.Run(() => SpinWait.SpinUntil(() =>
        {
            lock (router) return peer.IncomingStreams == 1;
        }, TimeSpan.FromSeconds(5))), Is.True);
        Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Does.Contain(address.GetPeerId()));
        Assert.That(BucketCounts(router).Values, Is.EquivalentTo(new[] { 1 }));
        await Disconnect(router, address, last);

        Assert.That(BucketCounts(router), Is.Empty);
    }

    [TestCase("/ip4/198.51.100.1/tcp/4001", -11)]
    [TestCase("/dns4/example.org/tcp/4001", -10)]
    [TestCase("/ip4/192.0.2.1/tcp/4001/p2p-circuit", -10)]
    [TestCase("", -10)]
    public async Task ReconnectUsesTheNewAddressAndRetainsOtherPenalties(string nextEndpoint, double expected)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress first = Address(1, "/ip4/192.0.2.1/tcp/4001");
        Multiaddress[] oldNeighbors = [Address(2, "/ip4/192.0.2.1/tcp/4001"), Address(3, "/ip4/192.0.2.1/tcp/4001")];
        Multiaddress[] newNeighbors = [Address(4, "/ip4/198.51.100.1/tcp/4001"), Address(5, "/ip4/198.51.100.1/tcp/4001")];
        TaskCompletionSource connection = Connect(router, first);
        foreach (Multiaddress address in oldNeighbors.Concat(newNeighbors)) Connect(router, address);
        router.SetAppSpecificScore(first.GetPeerId()!, -1);
        AssertScores(router, [first], -11);

        await Disconnect(router, first, connection);
        Assert.That(router.RetainedScoreCount, Is.EqualTo(1));
        AssertScores(router, oldNeighbors, 0);
        Multiaddress next = Address(1, nextEndpoint);
        Connect(router, next, inbound: true);

        AssertScores(router, [next], expected);
        AssertScores(router, oldNeighbors, 0);
        AssertScores(router, newNeighbors, expected == -11 ? -1 : 0);
    }

    [Test]
    public async Task RemovingAnOldGenerationCannotDecrementAReconnectedPeersBucket()
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress address = Address(1, "/ip4/192.0.2.1/tcp/4001");
        TaskCompletionSource outbound = Connect(router, address);
        PubsubRouter.PubsubPeer old = Peer(router, address);
        await Disconnect(router, address, outbound);
        Connect(router, address);
        // Teardown and stream completion can both remove the same generation; repeat removal after reconnect.
        typeof(PubsubRouter).GetMethod("RemovePeer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(router, [address.GetPeerId()!, address, old]);

        Assert.That(BucketCounts(router).Values, Is.EquivalentTo(new[] { 1 }));
        Multiaddress[] neighbors = [Address(2, "/ip4/192.0.2.1/tcp/4001"), Address(3, "/ip4/192.0.2.1/tcp/4001")];
        foreach (Multiaddress neighbor in neighbors) Connect(router, neighbor);
        AssertScores(router, neighbors.Append(address), -1);
    }

    private static PubsubRouter CreateRouter(PubsubSettings? settings = null) => new(new PeerStore(), settings ?? new PubsubSettings
    {
        IPColocationFactorThreshold = 2,
        IPColocationFactorWeight = -1,
        AutoConnect = false
    });

    private static PubsubSettings WhitelistedSettings(string network)
    {
        PubsubSettings settings = new()
        {
            AutoConnect = false,
            IPColocationFactorThreshold = 2,
            IPColocationFactorWeight = -1
        };
        settings.AddIPColocationWhitelist(network);
        return settings;
    }

    private static Multiaddress Address(int peer, string endpoint = "") => $"{endpoint}/p2p/{TestPeers.PeerId(peer)}";

    private static TaskCompletionSource Connect(PubsubRouter router, Multiaddress address, bool inbound = false)
    {
        TaskCompletionSource lifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = inbound
            ? router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV11, lifetime.Task, () => Task.CompletedTask).Token
            : router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV11, lifetime.Task, _ => { });
        Assert.That(token.IsCancellationRequested, Is.False);
        return lifetime;
    }

    private static void AssertScores(PubsubRouter router, IEnumerable<Multiaddress> addresses, double expected)
    {
        foreach (Multiaddress address in addresses)
        {
            Assert.That(router.GetPeerScore(address.GetPeerId()!), Is.EqualTo(expected), address.ToString());
        }
    }

    private static async Task Disconnect(PubsubRouter router, Multiaddress address, TaskCompletionSource lifetime)
    {
        lifetime.SetResult();
        Assert.That(await Task.Run(() => SpinWait.SpinUntil(
            () => !((IRoutingStateContainer)router).ConnectedPeers.Contains(address.GetPeerId()!), TimeSpan.FromSeconds(5))), Is.True);
        lock (router) { } // Wait for count updates and score retention in the same removal critical section.
    }

    private static Dictionary<string, int> BucketCounts(PubsubRouter router)
    {
        lock (router)
        {
            return new((Dictionary<string, int>)typeof(PubsubRouter)
                .GetField("_ipColocationCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(router)!);
        }
    }

    private static PubsubRouter.PubsubPeer Peer(PubsubRouter router, Multiaddress address) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<PeerId, PubsubRouter.PubsubPeer>)typeof(PubsubRouter)
            .GetField("peerState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(router)!)[address.GetPeerId()!];
}
