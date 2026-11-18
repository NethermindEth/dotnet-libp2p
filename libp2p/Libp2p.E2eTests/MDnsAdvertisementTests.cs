// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;
using System.Net;
using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Libp2p.E2eTests;

/// <summary>
/// Advertisement coverage for mDNS discovery. Before this, the only test for this protocol was
/// about disposal, which is why an IPv6 listener could abort advertisement unnoticed.
/// </summary>
[TestFixture]
[CancelAfter(30_000)]
public class MDnsAdvertisementTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly string Peer = TestPeers.PeerId(0).ToString();

    private static readonly IPAddress[] HostAddresses =
    [
        IPAddress.Parse("192.168.1.5"),
        IPAddress.Parse("10.1.2.3"),
        IPAddress.Parse("2001:db8::1"),
        IPAddress.Parse("fe80::9a2a:3ebf:3af8:4b10%3"),
    ];

    private static string[] Advertise(string listenAddress, params IPAddress[] hostAddresses) =>
        MDnsDiscoveryProtocol
            .AddressesToAdvertise(Multiaddress.Decode(listenAddress), hostAddresses.Length == 0 ? HostAddresses : hostAddresses)
            .Select(a => a.ToString())
            .ToArray();

    [Test]
    public void ConcreteAddressesAreAdvertisedUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Advertise($"/ip4/192.0.2.1/tcp/4001/p2p/{Peer}"),
                Is.EqualTo(new[] { $"/ip4/192.0.2.1/tcp/4001/p2p/{Peer}" }));
            // This shape threw NullReferenceException before: Get<IP4>() returns null for IPv6.
            Assert.That(Advertise($"/ip6/2001:db8:abcd::1/tcp/4001/p2p/{Peer}"),
                Is.EqualTo(new[] { $"/ip6/2001:db8:abcd::1/tcp/4001/p2p/{Peer}" }));
        });
    }

    [Test]
    public void AnIpv4WildcardExpandsToIpv4HostAddressesOnly()
    {
        Assert.That(Advertise($"/ip4/0.0.0.0/tcp/4001/p2p/{Peer}"), Is.EqualTo(new[]
        {
            $"/ip4/192.168.1.5/tcp/4001/p2p/{Peer}",
            $"/ip4/10.1.2.3/tcp/4001/p2p/{Peer}",
        }));
    }

    [Test]
    public void AnIpv6WildcardExpandsToIpv6HostAddressesWithoutTheScopeId()
    {
        // The scope id is this host's interface index; a receiver cannot use it.
        Assert.That(Advertise($"/ip6/::/tcp/4001/p2p/{Peer}"), Is.EqualTo(new[]
        {
            $"/ip6/2001:db8::1/tcp/4001/p2p/{Peer}",
            $"/ip6/fe80::9a2a:3ebf:3af8:4b10/tcp/4001/p2p/{Peer}",
        }));
    }

    [Test]
    public void AWildcardWithNoHostAddressOfItsFamilyIsDroppedNotAdvertised()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Advertise($"/ip6/::/tcp/4001/p2p/{Peer}", IPAddress.Parse("192.168.1.5")), Is.Empty);
            Assert.That(Advertise($"/ip4/0.0.0.0/tcp/4001/p2p/{Peer}", IPAddress.Parse("2001:db8::1")), Is.Empty);
        });
    }

    /// <summary>
    /// ToEndPoint reports 0.0.0.0 for a name-based address, so without a check for an IP layer a
    /// /dns4 listener would be mistaken for a wildcard and advertised as this host's own IPs.
    /// </summary>
    [TestCase("/dns4/example.org/tcp/4001")]
    [TestCase("/dns6/example.org/tcp/4001")]
    [TestCase("/dnsaddr/example.org")]
    public void NameBasedAddressesAreNeverExpanded(string listenAddress)
    {
        Assert.That(Advertise(listenAddress), Is.Empty);
    }

    [Test]
    public void TheTransportLayerSurvivesExpansion()
    {
        Assert.That(Advertise($"/ip4/0.0.0.0/udp/4001/quic-v1/p2p/{Peer}", IPAddress.Parse("192.168.1.5")),
            Is.EqualTo(new[] { $"/ip4/192.168.1.5/udp/4001/quic-v1/p2p/{Peer}" }));
    }

    [Test]
    public void AWildcardWithoutATransportLayerIsDropped()
    {
        Assert.That(Advertise("/ip4/0.0.0.0"), Is.Empty);
    }

    [Test]
    public void TheDefaultServiceNameIsTheLibp2pStandardOne()
    {
        Assert.That(new MDnsDiscoverySettings().ServiceName, Is.EqualTo("_p2p._udp.local"));
        Assert.That(MDnsDiscoverySettings.Libp2pServiceName, Is.EqualTo("_p2p._udp.local"));
    }

    /// <summary>
    /// ServiceProfile and QueryServiceInstances append the mDNS domain themselves, so a setting
    /// that already carries it must be normalized or the service becomes `_p2p._udp.local.local`,
    /// which no standard peer queries. Either spelling of a name has to reach the wire the same way.
    /// </summary>
    [TestCase("_p2p._udp.local", "_p2p._udp")]
    [TestCase("_p2p._udp.local.", "_p2p._udp")]
    [TestCase("_p2p._udp.LOCAL", "_p2p._udp")]
    [TestCase("_p2p._udp", "_p2p._udp")]
    [TestCase("pubsub-chat-example", "pubsub-chat-example")]
    [TestCase("pubsub-chat-example.local", "pubsub-chat-example")]
    public void TheMDnsDomainIsStrippedBeforeTheLibrarySeesTheName(string configured, string expected)
    {
        Assert.That(MDnsDiscoveryProtocol.WithoutMDnsDomain(configured), Is.EqualTo(expected));
    }

    /// <summary>The names that actually go on the wire, not the configured string.</summary>
    [TestCase(null, "_p2p._udp.local")]
    [TestCase("_p2p._udp.local", "_p2p._udp.local")]
    [TestCase("_p2p._udp", "_p2p._udp.local")]
    [TestCase("pubsub-chat-example", "pubsub-chat-example.local")]
    public void TheGeneratedDnsNamesCarryTheMDnsDomainExactlyOnce(string? configured, string expectedService)
    {
        string name = MDnsDiscoveryProtocol.WithoutMDnsDomain(configured ?? new MDnsDiscoverySettings().ServiceName);
        ServiceProfile profile = new("instance", name, 0);

        Assert.Multiple(() =>
        {
            Assert.That(profile.QualifiedServiceName.ToString(), Is.EqualTo(expectedService));
            Assert.That(profile.FullyQualifiedName.ToString(), Is.EqualTo($"instance.{expectedService}"));
            Assert.That(profile.QualifiedServiceName.ToString(), Does.Not.Contain(".local.local"));
        });
    }

    [TestCase(null, "_p2p._udp.local", "_p2p._udp")]
    [TestCase("pubsub-chat-example", "pubsub-chat-example.local", "pubsub-chat-example")]
    public async Task TheServiceIsAdvertisedAndQueriedUnderTheSameWireName(string? configured, string expectedService, string expectedQuery)
    {
        Capture capture = new();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        MDnsDiscoverySettings? settings = configured is null ? null : new() { ServiceName = configured };

        await using MDnsDiscoveryProtocol discovery = new(new PeerStore(), factory, settings);
        await discovery.StartDiscoveryAsync([Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{Peer}")]);
        await capture.WaitForAsync(m => m.StartsWith("Querying "), Timeout);

        string records = capture.Snapshot().First(m => m.StartsWith("DNS records to share"));
        Assert.Multiple(() =>
        {
            Assert.That(capture.Snapshot(), Has.Some.EqualTo($"Started as {Name(capture)} {expectedService}"));
            Assert.That(capture.Snapshot(), Has.Some.EqualTo($"Querying {expectedQuery}"));
            // The record names are what a remote peer matches on.
            Assert.That(records, Does.Contain($".{expectedService} "));
            Assert.That(records, Does.Not.Contain(".local.local"));
        });
    }

    /// <summary>
    /// The regression for the reported bug: any IPv6 listen address used to throw inside the
    /// setup block, which was caught and logged, so advertisement was skipped while the query
    /// loop still ran. A mixed listener lost its IPv4 advertisement with it.
    /// </summary>
    [TestCase("ipv6 only", new[] { "/ip6/::1/tcp/4001" })]
    [TestCase("mixed, ipv4 first", new[] { "/ip4/127.0.0.1/tcp/4001", "/ip6/::1/tcp/4002" })]
    [TestCase("mixed, ipv6 first", new[] { "/ip6/::1/tcp/4002", "/ip4/127.0.0.1/tcp/4001" })]
    [TestCase("ipv4 only", new[] { "/ip4/127.0.0.1/tcp/4001" })]
    public async Task ListenersOfEitherFamilyStillAdvertise(string label, string[] listenAddresses)
    {
        Capture capture = new();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        await using MDnsDiscoveryProtocol discovery = new(new PeerStore(), factory);

        Multiaddress[] local = listenAddresses.Select(a => Multiaddress.Decode($"{a}/p2p/{Peer}")).ToArray();
        await discovery.StartDiscoveryAsync(local);
        await capture.WaitForAsync(m => m.StartsWith("DNS records to share"), Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(capture.Errors, Is.Empty, $"{label}: setting up mDNS logged an error");
            string records = capture.Snapshot().First(m => m.StartsWith("DNS records to share"));
            foreach (string expected in listenAddresses)
            {
                Assert.That(records, Does.Contain($"dnsaddr={expected}/p2p/{Peer}"), $"{label}: {expected} was not advertised");
            }
        });
    }

    /// <summary>
    /// The production address source, which the injected-address cases above cannot cover. The
    /// library helper this used to call returns an IPv6 address only when it is link-local, so an
    /// IPv6 wildcard advertised `fe80::` endpoints alone — and go-libp2p rejects those in
    /// `Swarm.CanDial` before dialing, leaving an IPv6-only listener unreachable.
    /// </summary>
    [Test]
    public void TheHostAddressSourceIsNotLimitedToLinkLocalIpv6()
    {
        IPAddress[] ours = MDnsDiscoveryProtocol.HostAddresses();
        IPAddress[] libraryHelper = [.. MulticastService.GetLinkLocalAddresses()];

        // Pins the library behavior being worked around, so a change upstream shows up here.
        Assert.That(libraryHelper.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6),
            Has.All.Matches<IPAddress>(a => a.IsIPv6LinkLocal),
            "MulticastService.GetLinkLocalAddresses only yields link-local IPv6");

        Assert.Multiple(() =>
        {
            Assert.That(ours, Is.SupersetOf(libraryHelper), "the source must never offer fewer addresses");
            Assert.That(ours, Has.None.Matches<IPAddress>(IPAddress.IsLoopback), "loopback cannot be advertised");
        });

        IPAddress[] routableIpv6 = [.. RoutableIpv6OfThisHost()];

        if (routableIpv6.Length == 0)
        {
            Assert.Ignore("this host has no ULA or global IPv6 address, so there is nothing to miss");
        }

        Assert.That(ours, Is.SupersetOf(routableIpv6),
            "a ULA or global IPv6 address is exactly what go-libp2p will dial, so it must be offered");
    }

    /// <summary>End to end: an IPv6 wildcard must advertise more than link-local on such a host.</summary>
    [Test]
    public void AnIpv6WildcardAdvertisesRoutableAddressesOfThisHost()
    {
        // The expectation comes from the machine, not from HostAddresses, so narrowing that source
        // fails this test instead of quietly removing its subject.
        IPAddress[] routableIpv6 = [.. RoutableIpv6OfThisHost()];
        if (routableIpv6.Length == 0)
        {
            Assert.Ignore("this host has no ULA or global IPv6 address");
        }

        string[] advertised = MDnsDiscoveryProtocol
            .AddressesToAdvertise(Multiaddress.Decode($"/ip6/::/tcp/4001/p2p/{Peer}"), MDnsDiscoveryProtocol.HostAddresses())
            .Select(a => a.ToString())
            .ToArray();

        Assert.That(advertised, Is.SupersetOf(routableIpv6.Select(a => $"/ip6/{a}/tcp/4001/p2p/{Peer}")));
    }

    /// <summary>Ground truth, read from the operating system rather than from the code under test.</summary>
    private static IEnumerable<IPAddress> RoutableIpv6OfThisHost() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !IPAddress.IsLoopback(a));

    /// <summary>
    /// Both constraints at once. go-libp2p's resolver reads its addresses from its mDNS client, which
    /// assigns the instance's text per TXT record rather than appending
    /// (<c>entries[rr.Hdr.Name].Text = rr.Txt</c>), so separate records leave a Go peer with only
    /// the last address; and a single record that overflows the response is dropped whole by the
    /// sender. One record, bounded.
    /// </summary>
    [Test]
    public async Task EveryAddressGoesInOneRecordSoAGoPeerSeesThemAll()
    {
        Capture capture = new();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        await using MDnsDiscoveryProtocol discovery = new(new PeerStore(), factory);

        string[] listeners = ["/ip4/127.0.0.1/tcp/4001", "/ip4/127.0.0.2/tcp/4002", "/ip6/::1/tcp/4003"];
        await discovery.StartDiscoveryAsync([.. listeners.Select(a => Multiaddress.Decode($"{a}/p2p/{Peer}"))]);
        await capture.WaitForAsync(m => m.StartsWith("DNS records to share"), Timeout);

        string records = capture.Snapshot().First(m => m.StartsWith("DNS records to share"));
        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(records, "dnsaddr=").Count, Is.EqualTo(listeners.Length), "every address advertised");
            Assert.That(Regex.Matches(records, "IN TXT").Count, Is.EqualTo(1), "all of them in one TXT record");
        });
    }

    /// <summary>The bound exists so a grouped record can never overflow the response.</summary>
    [Test]
    public void TheAdvertisementIsBoundedAndFitsTheResponse()
    {
        Multiaddress[] many = [.. Enumerable.Range(0, 400).Select(i =>
            Multiaddress.Decode($"/ip6/2409:4090:b093:{i:x4}:3567:1626:4242:45fc/udp/4001/webrtc-direct/certhash/uEiDDq4_xNyDorZBH3TlGazyJdOWSwvo4PUo5YHFMrvDE8g/p2p/{Peer}"))];

        List<string> advertised = MDnsDiscoveryProtocol.BoundedAdvertisement(many);

        ServiceProfile profile = new("abcdefghijklmnopqrstuvwxyz012345", "_p2p._udp", 0,
            [IPAddress.Parse("192.168.31.129"), IPAddress.Parse("2409:4090:b093:6601::45fc")]);
        profile.Resources.RemoveAll(r => r is TXTRecord);
        profile.Resources.Add(new TXTRecord { Name = profile.FullyQualifiedName, Strings = advertised });
        Message message = new();
        foreach (ResourceRecord record in profile.Resources)
        {
            message.AdditionalRecords.Add(record);
        }

        int bytes = message.ToByteArray().Length;
        Assert.Multiple(() =>
        {
            Assert.That(advertised, Is.Not.Empty.And.Count.LessThan(many.Length), "the bound has to bite here");
            Assert.That(bytes, Is.LessThanOrEqualTo(8952), $"the response must fit, was {bytes}");
            message.Truncate(8952);
            Assert.That(message.AdditionalRecords.OfType<TXTRecord>().SelectMany(r => r.Strings).Count(),
                Is.EqualTo(advertised.Count), "and so must survive truncation untouched");
        });
    }

    /// <summary>
    /// The response has two halves and only one of them was bounded. ServiceProfile turns every
    /// host address into an A or AAAA record, so a host with many addresses overflows the response
    /// even when the TXT set is inside its own budget -- and MulticastService drops whole records
    /// to truncate, taking the grouped TXT record with the dial addresses in it.
    /// </summary>
    [Test]
    public void TheHostAddressRecordsShareTheResponseBudget()
    {
        IPAddress[] many =
        [
            .. Enumerable.Range(0, 150).Select(i => IPAddress.Parse($"10.{i / 256}.{i % 256}.7")),
            .. Enumerable.Range(0, 150).Select(i => IPAddress.Parse($"2001:db8:{i:x4}::7")),
        ];
        Multiaddress[] listeners = [.. Enumerable.Range(0, 40).Select(i =>
            Multiaddress.Decode($"/ip4/10.0.0.7/tcp/{4001 + i}/p2p/{Peer}"))];

        Message message = Advertisement(many, listeners);
        int bytes = message.ToByteArray().Length;
        message.Truncate(8952);
        int survived = message.AdditionalRecords.OfType<TXTRecord>().SelectMany(r => r.Strings).Count();

        Assert.Multiple(() =>
        {
            Assert.That(bytes, Is.LessThanOrEqualTo(8952), $"the whole response must fit, was {bytes}");
            Assert.That(survived, Is.GreaterThan(0), "a truncated response must still carry dial addresses");
        });
    }

    /// <summary>
    /// When the host-address records have to be trimmed, the ones a peer cannot reach go first:
    /// go-libp2p rejects link-local in Swarm.CanDial, so a surviving link-local record would have
    /// displaced a usable one.
    /// </summary>
    [Test]
    public void TrimmingDropsLinkLocalHostRecordsBeforeRoutableOnes()
    {
        IPAddress[] routable =
            [IPAddress.Parse("192.168.31.129"), IPAddress.Parse("2001:db8::7"), IPAddress.Parse("fd00::9")];
        IPAddress[] many =
        [
            .. Enumerable.Range(0, 150).Select(i => IPAddress.Parse($"169.254.{i / 256}.{i % 256 | 1}")),
            .. routable,
            .. Enumerable.Range(0, 150).Select(i => IPAddress.Parse($"fe80::{i:x4}")),
        ];
        Multiaddress[] listeners = [.. Enumerable.Range(0, 40).Select(i =>
            Multiaddress.Decode($"/ip4/192.168.31.129/tcp/{4001 + i}/p2p/{Peer}"))];

        Message message = Advertisement(many, listeners);
        List<IPAddress> kept = [.. message.AdditionalRecords.OfType<AddressRecord>().Select(r => r.Address)];

        Assert.Multiple(() =>
        {
            Assert.That(kept, Is.Not.Empty.And.Count.LessThan(many.Length), "trimming has to bite here");
            // Trimming stops as soon as the response fits, so link-local records may well survive.
            // What must never happen is a routable one being dropped while any of them is kept.
            Assert.That(kept, Is.SupersetOf(routable),
                "every reachable address must outlive every link-local one");
        });
    }

    /// <summary>
    /// The sizing has to cover the reply a browsing peer actually receives. ServiceDiscovery
    /// catalogues a service-to-instance PTR of its own, so measuring only the profile's resources
    /// under-counts the reply and lets Makaretu truncate a response this code called a fit.
    /// <para>
    /// Swept rather than measured at one host-address count. Trimming stops at the first size that
    /// fits, so where it lands relative to the limit depends on the size of the last record it
    /// removed -- a single fixture can miss the shortfall by luck.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheActualPtrReplyFitsTheResponseLimit()
    {
        Multiaddress[] listeners = [.. Enumerable.Range(0, 40).Select(i =>
            Multiaddress.Decode($"/ip4/10.0.0.7/tcp/{4001 + i}/p2p/{Peer}"))];
        List<string> oversized = [];

        for (int n = 120; n <= 200; n += 4)
        {
            IPAddress[] many =
            [
                .. Enumerable.Range(0, n).Select(i => IPAddress.Parse($"10.{i / 256}.{i % 256}.7")),
                .. Enumerable.Range(0, n).Select(i => IPAddress.Parse($"2001:db8:{i:x4}::7")),
            ];

            ServiceProfile profile = Profile(many, listeners);
            using ServiceDiscovery sd = new();
            sd.Advertise(profile);

            Message query = new();
            query.Questions.Add(new Question { Name = profile.QualifiedServiceName, Type = DnsType.PTR });
            Message reply = await sd.NameServer.ResolveAsync(query);
            reply.Questions.Clear();

            int bytes = reply.ToByteArray().Length;
            if (bytes > 8952)
            {
                oversized.Add($"{2 * n} host addresses -> {bytes} bytes");
            }

            Assert.That(reply.Answers.Concat(reply.AdditionalRecords).OfType<TXTRecord>()
                .SelectMany(record => record.Strings), Is.Not.Empty, "the reply must carry dial addresses");
        }

        Assert.That(oversized, Is.Empty, "every reply a peer receives must fit the response limit");
    }

    /// <summary>A peer offered under another service name must not enter discovery.</summary>
    [Test]
    public void OnlyInstancesOfTheConfiguredServiceAreAccepted()
    {
        DomainName configured = new("_p2p._udp.local");
        DomainName instance = new("alpha._p2p._udp.local");
        DomainName other = new("pubsub-chat-example._udp.local");

        Message ours = Reply(configured, instance, TimeSpan.FromMinutes(1));
        Message theirs = Reply(other, new DomainName("alpha.pubsub-chat-example._udp.local"), TimeSpan.FromMinutes(1));
        Message goodbye = Reply(configured, instance, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(MDnsDiscoveryProtocol.IsFromService(ours, configured, instance), Is.True,
                "an instance of the configured service is ours");
            Assert.That(MDnsDiscoveryProtocol.IsFromService(theirs, configured,
                new DomainName("alpha.pubsub-chat-example._udp.local")), Is.False,
                "another service's instance must be ignored");
            Assert.That(MDnsDiscoveryProtocol.IsFromService(goodbye, configured, instance), Is.False,
                "a goodbye must not import a departing peer");
        });
    }

    private static Message Reply(DomainName service, DomainName instance, TimeSpan ttl)
    {
        Message message = new();
        message.Answers.Add(new PTRRecord { Name = service, DomainName = instance, TTL = ttl });
        return message;
    }

    private static ServiceProfile Profile(IPAddress[] hostAddresses, Multiaddress[] listeners)
    {
        List<string> advertised = MDnsDiscoveryProtocol.BoundedAdvertisement(
            listeners.SelectMany(addr => MDnsDiscoveryProtocol.AddressesToAdvertise(addr, hostAddresses)));
        ServiceProfile profile = new("abcdefghijklmnopqrstuvwxyz012345", "_p2p._udp", 0, hostAddresses);
        profile.Resources.RemoveAll(r => r is TXTRecord);
        if (advertised.Count != 0)
        {
            profile.Resources.Add(new TXTRecord { Name = profile.FullyQualifiedName, Strings = advertised });
        }
        MDnsDiscoveryProtocol.TrimToResponseLimit(profile);
        return profile;
    }

    /// <summary>
    /// Covers the expansion half of the loopback case: given a loopback-only address source, a
    /// wildcard listener still yields a usable address.
    /// <para>
    /// The source half -- that <see cref="MDnsDiscoveryProtocol.HostAddresses"/> returns loopback on
    /// a host with no operational non-loopback interface -- is delegated to Makaretu's own interface
    /// selection and is not asserted here: this machine has non-loopback interfaces, so both the old
    /// and the new source return the same set and no test run here can tell them apart.
    /// </para>
    /// </summary>
    [Test]
    public void ALoopbackOnlyAddressSourceStillAdvertisesAWildcardListener()
    {
        Multiaddress wildcard = Multiaddress.Decode($"/ip4/0.0.0.0/tcp/4001/p2p/{Peer}");

        Assert.Multiple(() =>
        {
            Assert.That(MDnsDiscoveryProtocol.AddressesToAdvertise(wildcard, [IPAddress.Loopback])
                .Select(address => address.ToString()),
                Is.EqualTo(new[] { $"/ip4/127.0.0.1/tcp/4001/p2p/{Peer}" }));
            Assert.That(MDnsDiscoveryProtocol.HostAddresses(), Is.Not.Empty,
                "the address source must never be empty on a host with any operational interface");
        });
    }

    /// <summary>Builds the response exactly as StartDiscoveryAsync does.</summary>
    private static Message Advertisement(IPAddress[] hostAddresses, Multiaddress[] listeners)
    {
        ServiceProfile profile = Profile(hostAddresses, listeners);
        Message message = new();
        foreach (ResourceRecord record in profile.Resources)
        {
            message.AdditionalRecords.Add(record);
        }
        return message;
    }

    /// <summary>If the bound ever bites it must drop what a peer is least likely to reach.</summary>
    [Test]
    public void RoutableAddressesAreOfferedBeforeLinkLocalOnes()
    {
        Multiaddress[] mixed =
        [
            Multiaddress.Decode($"/ip6/fe80::1/tcp/4001/p2p/{Peer}"),
            Multiaddress.Decode($"/ip4/169.254.3.4/tcp/4002/p2p/{Peer}"),
            Multiaddress.Decode($"/ip4/192.168.1.5/tcp/4003/p2p/{Peer}"),
            Multiaddress.Decode($"/ip6/2001:db8::1/tcp/4004/p2p/{Peer}"),
        ];

        Assert.That(MDnsDiscoveryProtocol.BoundedAdvertisement(mixed), Is.EqualTo(new[]
        {
            $"dnsaddr=/ip4/192.168.1.5/tcp/4003/p2p/{Peer}",
            $"dnsaddr=/ip6/2001:db8::1/tcp/4004/p2p/{Peer}",
            $"dnsaddr=/ip6/fe80::1/tcp/4001/p2p/{Peer}",
            $"dnsaddr=/ip4/169.254.3.4/tcp/4002/p2p/{Peer}",
        }));
    }

    /// <summary>RFC 6763 section 6.1 caps one TXT string at 255 bytes.</summary>
    [Test]
    public void AnOversizedEntryIsNotAdvertised()
    {
        string relays = string.Concat(Enumerable.Repeat($"/p2p/{Peer}/p2p-circuit", 4));
        Multiaddress huge = Multiaddress.Decode($"/ip4/192.0.2.1/tcp/4001{relays}/p2p/{Peer}");
        Multiaddress fine = Multiaddress.Decode($"/ip4/192.0.2.2/tcp/4002/p2p/{Peer}");

        Assert.That($"dnsaddr={huge}".Length, Is.GreaterThan(255), "the fixture has to exceed the limit");
        Assert.That(MDnsDiscoveryProtocol.BoundedAdvertisement([huge, fine]),
            Is.EqualTo(new[] { $"dnsaddr={fine}" }));
    }

    /// <summary>
    /// A standard peer advertises transports this build cannot decode — go-libp2p enables
    /// WebTransport by default — and one of those must not cost us the peer's usable addresses.
    /// </summary>
    [Test]
    public void AnUnreadableAdvertisedAddressDoesNotDiscardThePeer()
    {
        const string hash = "uEiDDq4_xNyDorZBH3TlGazyJdOWSwvo4PUo5YHFMrvDE8g";
        string[] advertised =
        [
            $"dnsaddr=/ip4/192.0.2.1/udp/4001/quic-v1/webtransport/certhash/{hash}/p2p/{Peer}",
            $"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{Peer}",
            $"dnsaddr=/ip4/192.0.2.1/udp/4002/quic-v1/p2p/{Peer}",
            "version=1", // a TXT string that is not an address at all
        ];

        List<Multiaddress> decoded = MDnsDiscoveryProtocol.DecodeAdvertised(advertised);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Select(a => a.ToString()), Is.EqualTo(new[]
            {
                $"/ip4/192.0.2.1/tcp/4001/p2p/{Peer}",
                $"/ip4/192.0.2.1/udp/4002/quic-v1/p2p/{Peer}",
            }));
            Assert.That(decoded.Select(a => a.GetPeerId()!.ToString()), Has.All.EqualTo(Peer),
                "the peer identity still has to be recoverable from what did decode");
        });
    }

    /// <summary>A concrete listener can carry a scope id, and go-multiaddr rejects a scoped string.</summary>
    [TestCase("/ip6/fe80::1234%3/tcp/4001", "/ip6/fe80::1234/tcp/4001")]
    [TestCase("/ip6/fe80::1234/tcp/4001", "/ip6/fe80::1234/tcp/4001")]
    [TestCase("/ip4/192.0.2.1/tcp/4001", "/ip4/192.0.2.1/tcp/4001")]
    public void AConcreteAddressIsAdvertisedWithoutItsScopeId(string listener, string expected)
    {
        Assert.That(Advertise($"{listener}/p2p/{Peer}"), Is.EqualTo(new[] { $"{expected}/p2p/{Peer}" }));
    }

    private static int SurvivingAddresses(TXTRecord[] records)
    {
        Message message = new();
        foreach (TXTRecord record in records)
        {
            message.AdditionalRecords.Add(record);
        }

        message.Truncate(8952);
        return message.AdditionalRecords.OfType<TXTRecord>()
            .SelectMany(record => record.Strings)
            .Count(text => text.StartsWith("dnsaddr="));
    }

    /// <summary>
    /// Implementations differ on which section carries the TXT records. This one and go-libp2p use
    /// the additional section; js-libp2p puts the PTR and every dnsaddr TXT in the answer section.
    /// Reading one section only discovers such a peer and then drops it with an empty address list.
    /// </summary>
    [TestCase("answers", false)]
    [TestCase("additional", true)]
    public void AdvertisedStringsAreReadFromEitherRecordSection(string label, bool additional)
    {
        DomainName instance = new($"peer._p2p._udp.local");
        Message message = new();
        message.Answers.Add(new PTRRecord { Name = new DomainName("_p2p._udp.local"), DomainName = instance });
        TXTRecord txt = new() { Name = instance, Strings = [$"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{Peer}"] };
        (additional ? message.AdditionalRecords : message.Answers).Add(txt);

        IEnumerable<string> strings = MDnsDiscoveryProtocol.AdvertisedStrings(message, instance);

        Assert.That(strings, Is.EqualTo(new[] { $"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{Peer}" }), label);
    }

    [Test]
    public void RecordsOfAnotherInstanceInTheSameMessageAreIgnored()
    {
        DomainName mine = new("wanted._p2p._udp.local");
        DomainName other = new("other._p2p._udp.local");
        Message message = new();
        message.Answers.Add(new TXTRecord { Name = other, Strings = [$"dnsaddr=/ip4/198.51.100.9/tcp/9/p2p/{Peer}"] });
        message.AdditionalRecords.Add(new TXTRecord { Name = mine, Strings = [$"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{Peer}"] });

        Assert.That(MDnsDiscoveryProtocol.AdvertisedStrings(message, mine),
            Is.EqualTo(new[] { $"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{Peer}" }));
    }

    /// <summary>A root label and a different case name the same DNS record.</summary>
    [TestCase("peer._p2p._udp.local.")]
    [TestCase("PEER._P2P._UDP.LOCAL")]
    public void TheInstanceNameIsMatchedAsADnsName(string advertisedAs)
    {
        DomainName instance = new("peer._p2p._udp.local");
        Message message = new();
        message.Answers.Add(new TXTRecord { Name = new DomainName(advertisedAs), Strings = [$"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{Peer}"] });

        Assert.That(MDnsDiscoveryProtocol.AdvertisedStrings(message, instance), Is.Not.Empty);
    }

    /// <summary>
    /// The A and AAAA records the specification's "find all response" calls for must describe the
    /// same host as the dnsaddr entries beside them. Left to itself ServiceProfile fills them from
    /// the library's link-local-only helper, so the two halves of one response disagreed, and the
    /// Gotchas section says many tools query those records directly.
    /// </summary>
    [Test]
    public async Task TheAddressRecordsAndTheDnsaddrEntriesDescribeTheSameHost()
    {
        Capture capture = new();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        await using MDnsDiscoveryProtocol discovery = new(new PeerStore(), factory);

        await discovery.StartDiscoveryAsync([Multiaddress.Decode($"/ip4/0.0.0.0/tcp/4001/p2p/{Peer}"), Multiaddress.Decode($"/ip6/::/tcp/4001/p2p/{Peer}")]);
        await capture.WaitForAsync(m => m.StartsWith("DNS records to share"), Timeout);
        string records = capture.Snapshot().First(m => m.StartsWith("DNS records to share"));

        // Every address this host offers has to appear in an A or AAAA record, scope id stripped.
        foreach (IPAddress expected in MDnsDiscoveryProtocol.HostAddresses())
        {
            string text = (expected.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPAddress(expected.GetAddressBytes())
                : expected).ToString();
            Assert.That(records, Does.Contain(text), $"{text} is advertised as a dnsaddr but missing from the A/AAAA records");
        }

        Assert.That(records, Does.Not.Contain("%"), "a scope id must not reach the wire");
    }

    private sealed class Capture : ILoggerProvider, ILogger
    {
        private readonly List<string> _messages = [];
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors { get { lock (_messages) return _errors.ToArray(); } }

        public string[] Snapshot() { lock (_messages) return _messages.ToArray(); }

        public async Task WaitForAsync(Func<string, bool> predicate, TimeSpan timeout)
        {
            for (int i = 0; i < timeout.TotalMilliseconds / 20; i++)
            {
                if (Snapshot().Any(predicate)) return;
                await Task.Delay(20);
            }

            Assert.Fail($"no log line matched within {timeout}; saw: {string.Join(" | ", Snapshot())}");
        }

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, ex);
            lock (_messages)
            {
                _messages.Add(line);
                if (level >= LogLevel.Error) _errors.Add(line);
            }
        }
    }

    private static string Name(Capture capture) =>
        capture.Snapshot().First(m => m.StartsWith("Started as ")).Split(' ')[2];
}
