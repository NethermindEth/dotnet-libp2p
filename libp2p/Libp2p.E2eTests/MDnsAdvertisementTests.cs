// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;
using System.Net;
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
