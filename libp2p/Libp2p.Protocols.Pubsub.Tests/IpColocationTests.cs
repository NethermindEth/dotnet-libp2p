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
        Assert.That(IpColocation.GetKeys(Address(1, "/ip4/10.0.0.1/tcp/4001"), settings.IPColocationFactorWhitelist),
            Is.EqualTo(((string?)null, (string?)null)));
    }

    [TestCase("/ip4/192.0.2.1/tcp/4001", "192.0.2.1", null)]
    [TestCase("/ip6/::ffff:192.0.2.1/tcp/4001", "192.0.2.1", null)]
    [TestCase("/ip6/2001:db8:abcd:12::1/udp/4001/quic-v1", "2001:db8:abcd:12::1", "2001:db8:abcd:12::/64")]
    [TestCase("/ip6/2001:0DB8:ABCD:0012:0000:0000:0000:0002/tcp/4002", "2001:db8:abcd:12::2", "2001:db8:abcd:12::/64")]
    [TestCase("/ip4/10.0.0.1/tcp/4001", "10.0.0.1", null)]
    [TestCase("/ip4/172.16.0.1/tcp/4001", "172.16.0.1", null)]
    [TestCase("/ip4/192.168.1.1/tcp/4001", "192.168.1.1", null)]
    [TestCase("/ip6/fd12:3456:789a:1::1/tcp/4001", "fd12:3456:789a:1::1", "fd12:3456:789a:1::/64")]
    [TestCase("/ip4/127.0.0.1/tcp/4001", null, null)]
    [TestCase("/ip4/127.2.3.4/tcp/4001", null, null)]
    [TestCase("/ip6/::1/tcp/4001", null, null)]
    [TestCase("/ip6/::ffff:127.0.0.1/tcp/4001", null, null)]
    [TestCase("/ip4/0.0.0.0/tcp/4001", null, null)]
    [TestCase("/ip6/::/tcp/4001", null, null)]
    [TestCase("/ip6/::ffff:0.0.0.0/tcp/4001", null, null)]
    [TestCase("/ip6/fe80::1%3/tcp/4001", null, null)]
    [TestCase("/dns/example.org/tcp/4001", null, null)]
    [TestCase("/dns4/example.org/tcp/4001", null, null)]
    [TestCase("/dns6/example.org/tcp/4001", null, null)]
    [TestCase("/dnsaddr/example.org", null, null)]
    [TestCase("", null, null)]
    public void KeysNormalizeLiteralIpsAndExcludeUnusableAddresses(string endpoint, string? expectedAddress, string? expectedSubnet)
    {
        Assert.That(IpColocation.GetKeys(Address(1, endpoint), []), Is.EqualTo((expectedAddress, expectedSubnet)));
    }

    [Test]
    public void RelayTransportAndDestinationIpsAreNeverUsed()
    {
        Multiaddress relay = Address(99, "/ip4/192.0.2.1/tcp/4001");
        Assert.That(IpColocation.GetKeys(Address(1, $"{relay}/p2p-circuit"), []), Is.EqualTo(((string?)null, (string?)null)));
        Assert.That(IpColocation.GetKeys(Address(1, $"{relay}/p2p-circuit/ip4/198.51.100.1/tcp/4001"), []),
            Is.EqualTo(((string?)null, (string?)null)));
    }

    [TestCase("/ip4/192.0.2.1/tcp/4001", "192.0.2.0/24")]
    [TestCase("/ip6/::ffff:192.0.2.1/tcp/4001", "192.0.2.0/24")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "2001:db8::/32")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "2001:db8:1::1/128")]
    public void WhitelistChecksTheNormalizedAddressBeforeGrouping(string endpoint, string network)
    {
        Assert.That(IpColocation.GetKeys(Address(1, endpoint), WhitelistedSettings(network).IPColocationFactorWhitelist),
            Is.EqualTo(((string?)null, (string?)null)));
        Assert.That(IpColocation.GetKeys(Address(1, endpoint), WhitelistedSettings("198.51.100.0/24").IPColocationFactorWhitelist).Address,
            Is.Not.Null);
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

    [TestCase("2001:db8:1::1", "2001:0DB8:0001:0:0:0:0:1")]
    [TestCase("2001:db8:1::", "2001:0DB8:0001:0:0:0:0:0")]
    public async Task NativeIpv6ScoresBothExactAddressAndSubnet(string firstIp, string equivalentIp)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress[] addresses =
        [
            Address(1, $"/ip6/{firstIp}/tcp/4001"),
            Address(2, $"/ip6/{equivalentIp}/udp/5000/quic-v1"),
            Address(3, $"/ip6/{firstIp}/tcp/6000"),
            Address(4, $"/ip6/{equivalentIp}/tcp/7000")
        ];
        Connect(router, addresses[0]);
        Connect(router, addresses[1], inbound: true);
        AssertScores(router, addresses.Take(2), 0);

        Connect(router, addresses[2]);
        AssertScores(router, addresses.Take(3), -2);
        Connect(router, addresses[3], inbound: true);
        AssertScores(router, addresses, -8);

        Multiaddress neighbor = Address(5, "/ip6/2001:db8:1::2/tcp/4001");
        Connect(router, neighbor);
        AssertScores(router, addresses, -13); // exact-address surplus 2 squared + /64 surplus 3 squared
        AssertScores(router, [neighbor], -9); // only the shared /64 exceeds the threshold
    }

    [TestCase("/ip4/192.0.2.1/tcp/4001", "/ip6/::ffff:192.0.2.1/tcp/5000")]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "/ip6/2001:0DB8:0001:0:0:0:0:2/udp/5000/quic-v1")]
    [TestCase("/ip4/10.0.0.1/tcp/4001", "/ip4/10.0.0.1/tcp/5000")]
    public async Task NormalizedAddressesShareColocationBuckets(string firstEndpoint, string secondEndpoint)
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
        Assert.That(BucketCounts(router), Has.Count.EqualTo(6));
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

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task DisconnectRemovesOnlyTheDepartingPeerAndDeletesEmptyBuckets(bool inbound, bool ipv6)
    {
        await using PubsubRouter router = CreateRouter();
        string endpoint = ipv6 ? "/ip6/2001:db8:1::1/tcp/4001" : "/ip4/192.0.2.1/tcp/4001";
        Multiaddress[] addresses = Enumerable.Range(1, 3).Select(i => Address(i, endpoint)).ToArray();
        TaskCompletionSource[] connections = addresses.Select(address => Connect(router, address, inbound)).ToArray();
        AssertScores(router, addresses, ipv6 ? -2 : -1);

        await Disconnect(router, addresses[2], connections[2]);
        AssertScores(router, addresses.Take(2), 0);
        Assert.That(BucketCounts(router).Values, Is.EquivalentTo(ipv6 ? new[] { 2, 2 } : new[] { 2 }));

        await Disconnect(router, addresses[0], connections[0]);
        await Disconnect(router, addresses[1], connections[1]);
        Assert.That(BucketCounts(router), Is.Empty, "Retained scores must not count as connected peers.");
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task AdditionalStreamsKeepTheFirstAddressAndDoNotCountTwice(bool inboundFirst, bool ipv6)
    {
        await using PubsubRouter router = CreateRouter();
        string firstEndpoint = ipv6 ? "/ip6/2001:db8:1::1/tcp/4001" : "/ip4/192.0.2.1/tcp/4001";
        string changedEndpoint = ipv6 ? "/ip6/2001:db8:2::1/tcp/5000" : "/ip4/198.51.100.1/tcp/5000";
        Multiaddress first = Address(1, firstEndpoint);
        Connect(router, first, inboundFirst);
        Connect(router, Address(1, changedEndpoint), !inboundFirst);
        Connect(router, first, inbound: true);
        Multiaddress second = Address(2, firstEndpoint);
        Connect(router, second);
        AssertScores(router, [first, second], 0);

        Multiaddress third = Address(3, firstEndpoint);
        Connect(router, third);
        AssertScores(router, [first, second, third], ipv6 ? -2 : -1);
        Dictionary<string, int> expected = ipv6
            ? new() { ["2001:db8:1::1"] = 3, ["2001:db8:1::/64"] = 3 }
            : new() { ["192.0.2.1"] = 3 };
        Assert.That(BucketCounts(router), Is.EquivalentTo(expected));
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

    [TestCase("/ip4/192.0.2.1/tcp/4001", "/ip4/198.51.100.1/tcp/4001", -11)]
    [TestCase("/ip4/192.0.2.1/tcp/4001", "/dns4/example.org/tcp/4001", -10)]
    [TestCase("/ip4/192.0.2.1/tcp/4001", "/ip4/192.0.2.1/tcp/4001/p2p-circuit", -10)]
    [TestCase("/ip4/192.0.2.1/tcp/4001", "", -10)]
    [TestCase("/ip4/192.0.2.1/tcp/4001", "/ip6/2001:db8:2::1/tcp/4001", -12)]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "/ip4/198.51.100.1/tcp/4001", -11)]
    [TestCase("/ip6/2001:db8:1::1/tcp/4001", "/dns4/example.org/tcp/4001", -10)]
    public async Task ReconnectUsesTheNewAddressAndRetainsOtherPenalties(string oldEndpoint, string nextEndpoint, double expected)
    {
        await using PubsubRouter router = CreateRouter();
        Multiaddress first = Address(1, oldEndpoint);
        string targetEndpoint = nextEndpoint.StartsWith("/ip6/") ? nextEndpoint : "/ip4/198.51.100.1/tcp/4001";
        Multiaddress[] oldNeighbors = [Address(2, oldEndpoint), Address(3, oldEndpoint)];
        Multiaddress[] newNeighbors = [Address(4, targetEndpoint), Address(5, targetEndpoint)];
        TaskCompletionSource connection = Connect(router, first);
        foreach (Multiaddress address in oldNeighbors.Concat(newNeighbors)) Connect(router, address);
        router.SetAppSpecificScore(first.GetPeerId()!, -1);
        AssertScores(router, [first], oldEndpoint.StartsWith("/ip6/") ? -12 : -11);

        await Disconnect(router, first, connection);
        Assert.That(router.RetainedScoreCount, Is.EqualTo(1));
        AssertScores(router, oldNeighbors, 0);
        Multiaddress next = Address(1, nextEndpoint);
        Connect(router, next, inbound: true);

        AssertScores(router, [next], expected);
        AssertScores(router, oldNeighbors, 0);
        AssertScores(router, newNeighbors, expected + 10);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReconnectUsesNewIpv6AddressAndSubnet(bool sameSubnet)
    {
        await using PubsubRouter router = CreateRouter();
        const string oldEndpoint = "/ip6/2001:db8:1::1/tcp/4001";
        string newEndpoint = sameSubnet ? "/ip6/2001:db8:1::2/tcp/4001" : "/ip6/2001:db8:2::1/tcp/4001";
        Multiaddress first = Address(1, oldEndpoint);
        Multiaddress[] oldNeighbors = [Address(2, oldEndpoint), Address(3, oldEndpoint)];
        Multiaddress[] newNeighbors = [Address(4, newEndpoint), Address(5, newEndpoint)];
        TaskCompletionSource connection = Connect(router, first);
        foreach (Multiaddress neighbor in oldNeighbors.Concat(newNeighbors)) Connect(router, neighbor);
        router.SetAppSpecificScore(first.GetPeerId()!, -1);
        AssertScores(router, [first], sameSubnet ? -20 : -12);

        await Disconnect(router, first, connection);
        Assert.That(router.RetainedScoreCount, Is.EqualTo(1));
        AssertScores(router, oldNeighbors.Concat(newNeighbors), sameSubnet ? -4 : 0);
        Multiaddress next = Address(1, newEndpoint);
        Connect(router, next, inbound: true);

        AssertScores(router, [next], sameSubnet ? -20 : -12);
        AssertScores(router, oldNeighbors, sameSubnet ? -9 : 0);
        AssertScores(router, newNeighbors, sameSubnet ? -10 : -2);
        Assert.That(BucketCounts(router)["2001:db8:1::1"], Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RemovingAnOldGenerationCannotDecrementAReconnectedPeersBuckets(bool ipv6)
    {
        await using PubsubRouter router = CreateRouter();
        string oldEndpoint = ipv6 ? "/ip6/2001:db8:1::1/tcp/4001" : "/ip4/192.0.2.1/tcp/4001";
        string newEndpoint = ipv6 ? "/ip6/2001:db8:2::1/tcp/4001" : oldEndpoint;
        Multiaddress address = Address(1, oldEndpoint);
        TaskCompletionSource outbound = Connect(router, address);
        PubsubRouter.PubsubPeer old = Peer(router, address);
        await Disconnect(router, address, outbound);
        Multiaddress next = Address(1, newEndpoint);
        Connect(router, next);
        // Teardown and stream completion can both remove the same generation; repeat removal after reconnect.
        typeof(PubsubRouter).GetMethod("RemovePeer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(router, [address.GetPeerId()!, address, old]);

        Assert.That(BucketCounts(router).Values, Is.EquivalentTo(ipv6 ? new[] { 1, 1 } : new[] { 1 }));
        Multiaddress[] neighbors = [Address(2, newEndpoint), Address(3, newEndpoint)];
        foreach (Multiaddress neighbor in neighbors) Connect(router, neighbor);
        AssertScores(router, neighbors.Append(next), ipv6 ? -2 : -1);
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
