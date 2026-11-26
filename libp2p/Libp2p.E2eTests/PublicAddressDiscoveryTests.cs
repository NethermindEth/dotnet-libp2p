// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using NSubstitute;
using NUnit.Framework;
using System.Net;
using System.Net.Sockets;

namespace Libp2p.E2eTests;

public class PublicAddressDiscoveryTests
{
    private const AddressFamily IPv4 = AddressFamily.InterNetwork;
    private const AddressFamily IPv6 = AddressFamily.InterNetworkV6;
    private const string FirstObservedAddress = "/ip4/8.8.4.4/tcp/54321";
    private const string SecondObservedAddress = "/ip4/1.1.1.1/tcp/54322";
    private static readonly IPAddress FirstCandidate = IPAddress.Parse("8.8.4.4");
    private static readonly IPAddress SecondCandidate = IPAddress.Parse("1.1.1.1");

    [TestCase(true)]
    [TestCase(false)]
    public async Task AutomaticIdentifyCollectsOnlyAuthenticatedReports(bool authenticated)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        IConnectionProtocol security = authenticated ? new NoiseProtocol() : new PlainTextProtocol();
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(security)
            .AddLibp2p<ObservedAddressBuilder>(builder => builder.WithPublicAddressDiscovery())
            .BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer local = factory.Create(TestPeers.Identity(1));
        await using ILocalPeer firstReporter = factory.Create(TestPeers.Identity(2));
        await using ILocalPeer secondReporter = factory.Create(TestPeers.Identity(3));

        await firstReporter.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await secondReporter.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await local.DialAsync(firstReporter.ListenAddresses.Single(), deadline.Token).WaitAsync(deadline.Token);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);

        await local.DialAsync(secondReporter.ListenAddresses.Single(), deadline.Token).WaitAsync(deadline.Token);
        IPAddress? discovered = await discovery.GetCandidateAsync(IPv4);
        IPAddress? reporterCandidate = await firstReporter.GetPublicAddressDiscovery()!.GetCandidateAsync(IPv4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(discovered, Is.EqualTo(authenticated ? FirstCandidate : null));
            Assert.That(reporterCandidate, Is.Null, "Peers from one factory collect their own reports.");
            Assert.That(local.ListenAddresses, Is.Empty);
            Assert.That(firstReporter.ListenAddresses.Single().GetDirectIPAddress(), Is.EqualTo(IPAddress.Loopback));
        }
    }

    [Test]
    public async Task ReportsNeedDifferentIdentitiesAndIPv4Addresses()
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4001", Encode(FirstObservedAddress));
        await ReportAsync(local, 3, "/ip6/::ffff:11.1.1.1/tcp/4002", Encode(FirstObservedAddress));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null, "A mapped IPv4 reporter shares its original network group.");

        await ReportAsync(local, 3, "/ip4/11.1.1.2/tcp/4003", Encode(FirstObservedAddress));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null, "Changing addresses cannot give one identity an extra vote.");

        await ReportAsync(local, 4, "/ip4/11.1.1.3/tcp/4003", Encode("/ip6/::ffff:8.8.4.4/tcp/54320"));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.EqualTo(FirstCandidate));
        Assert.That(await discovery.GetCandidateAsync(IPv6), Is.Null);
    }

    [Test]
    public async Task IPv6UsesIndependentPrefixesAndAcceptsStandardUdp()
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;
        ByteString encoded = Encode("/ip6/2606:4700::1111/udp/54444/quic-v1");
        // Convert the bundled package's UDP code to multicodec 273.
        ByteString standardUdp = ByteString.CopyFrom([.. encoded.Span[..17], 0x91, 0x02, .. encoded.Span[18..]]);

        await ReportAsync(local, 2, "/ip6/2600:2000:2000:ab01::1/udp/4001", standardUdp);
        await ReportAsync(local, 3, "/ip6/2600:2000:2000:abfe::2/udp/4001", standardUdp);
        Assert.That(await discovery.GetCandidateAsync(IPv6), Is.Null, "Both reporters are in the same /56.");

        await ReportAsync(local, 4, "/ip6/2600:2000:2000:ac00::1/udp/4001", encoded);
        Assert.That(await discovery.GetCandidateAsync(IPv6), Is.EqualTo(IPAddress.Parse("2606:4700::1111")));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);
    }

    [Test]
    public async Task ConflictingReportsRequireAMajorityAndLatestReportsWin()
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4001", Encode(FirstObservedAddress));
        await ReportAsync(local, 3, "/ip4/11.1.1.2/tcp/4001", Encode(SecondObservedAddress));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);

        await ReportAsync(local, 4, "/ip4/11.1.1.3/tcp/4001", Encode(FirstObservedAddress));
        Assert.That(await discovery.GetCandidateAsync(IPv4, (_, _) => throw new InvalidOperationException("Unexpected fallback")), Is.EqualTo(FirstCandidate));

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4002", Encode(SecondObservedAddress));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.EqualTo(SecondCandidate));
    }

    [Test]
    public async Task ExpiresReportsAtTenMinutes()
    {
        ManualClock clock = new();
        await using ServiceProvider services = CreateServices(clock);
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;
        TimeSpan lifetime = TimeSpan.FromMinutes(10);

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4001", Encode(FirstObservedAddress));
        clock.Advance(TimeSpan.FromTicks(1));
        await ReportAsync(local, 3, "/ip4/11.1.1.2/tcp/4001", Encode(FirstObservedAddress));
        clock.Advance(lifetime - TimeSpan.FromTicks(2));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.EqualTo(FirstCandidate));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null, "The oldest report has reached the TTL.");
    }

    [Test]
    public async Task EvictsTheOldestReportAtTheFamilyCapacity()
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        for (int i = 1; i <= 32; i++)
        {
            string observed = i <= 17 ? FirstObservedAddress : SecondObservedAddress;
            await ReportAsync(local, 10 + i, $"/ip4/11.1.2.{i}/tcp/4001", Encode(observed));
        }
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.EqualTo(FirstCandidate));

        await ReportAsync(local, 43, "/ip4/11.1.2.33/tcp/4001", Encode("/ip4/9.9.9.9/tcp/9999"));
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null, "The new report evicts the oldest vote and ends the majority.");
    }

    private static IEnumerable<TestCaseData> InvalidObservations()
    {
        yield return new TestCaseData(ByteString.Empty).SetName("IgnoresMissingObservation");
        yield return new TestCaseData(ByteString.CopyFrom([4, 8, 8, 4, 4, 6, 1])).SetName("IgnoresTruncatedPort");
        yield return new TestCaseData(ByteString.CopyFrom([4, 8, 8, 4, 4, 6, 1, 1, 0])).SetName("IgnoresUnknownTrailingProtocol");
        yield return new TestCaseData(Encode("/ip4/8.8.4.4/tcp/0")).SetName("IgnoresZeroPort");
        yield return new TestCaseData(Encode("/ip4/192.168.0.1/tcp/4001")).SetName("IgnoresPrivateObservation");
        yield return new TestCaseData(Encode("/ip6/2001:db8::1/tcp/4001")).SetName("IgnoresDocumentationIpv6");
        yield return new TestCaseData(Encode("/ip6/2606:4700::1111/tcp/4001")).SetName("IgnoresDifferentConnectionAddressFamily");
        yield return new TestCaseData(Encode("/ip4/8.8.4.4/udp/4001")).SetName("IgnoresDifferentConnectionTransport");
        yield return new TestCaseData(Encode("/dns4/host.example/tcp/4001")).SetName("IgnoresObservedDnsName");
        yield return new TestCaseData(Encode("/ip4/8.8.4.4/tcp/4001/p2p-circuit")).SetName("IgnoresRelayedObservation");
        yield return new TestCaseData(Encode("/ip4/8.8.4.4/tcp/4001/ip4/1.1.1.1")).SetName("IgnoresMultipleObservedHosts");
    }

    [TestCaseSource(nameof(InvalidObservations))]
    public async Task MalformedOrNonPublicObservationDoesNotFailIdentifyOrCount(ByteString observation)
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4001", observation);
        IdentifyResult result = await ReportAsync(local, 3, "/ip4/11.1.1.2/tcp/4001", observation);
        Assert.That(result.ObservedAddress, Is.EqualTo(observation), "The typed Identify result still contains the raw bytes.");
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);
        Assert.That(await discovery.GetCandidateAsync(IPv6), Is.Null);
    }

    [TestCase(3, "/ip4/11.1.1.2/tcp/4001/p2p-circuit")]
    [TestCase(3, "/ip4/127.0.0.1/tcp/4001")]
    [TestCase(3, "/dns4/host.example/tcp/4001")]
    [TestCase(1, "/ip4/11.1.1.2/tcp/4001")]
    public async Task ReporterMustBeAnotherPeerWithAPublicDirectAddress(int reporter, string reporterAddress)
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4001", Encode(FirstObservedAddress));
        await ReportAsync(local, reporter, reporterAddress, Encode(FirstObservedAddress));

        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);
    }

    [Test]
    public async Task RejectedIdentifyCannotContributeAReport()
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        await ReportAsync(local, 2, "/ip4/11.1.1.1/tcp/4001", Encode(FirstObservedAddress));
        Assert.ThrowsAsync<PeerConnectionException>(async () => await ReportAsync(local, 3, "/ip4/11.1.1.2/tcp/4001",
            Encode(FirstObservedAddress), messageIdentity: TestPeers.Identity(4)));

        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);
    }

    [Test]
    public async Task EmptyDiscoveryUsesOnlyAnExplicitLookup()
    {
        await using Libp2pPeerFactoryBuilder defaultBuilder = new();
        await using ILocalPeer disabled = defaultBuilder.Build().Create();
        Assert.That(disabled.GetPublicAddressDiscovery(), Is.Null);

        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null);

        int lookups = 0;
        IPAddress? result = await discovery.GetCandidateAsync(IPv4, (family, _) =>
        {
            Assert.That(family, Is.EqualTo(IPv4));
            lookups++;
            return ValueTask.FromResult<IPAddress?>(IPAddress.Parse("::ffff:8.8.4.4"));
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(FirstCandidate));
            Assert.That(lookups, Is.EqualTo(1));
        }
        Assert.That(await discovery.GetCandidateAsync(IPv4), Is.Null, "External results are not retained as peer votes.");
        Assert.That(await discovery.GetCandidateAsync(IPv6, (_, _) => ValueTask.FromResult<IPAddress?>(FirstCandidate)), Is.Null);
        Assert.That(await discovery.GetCandidateAsync(IPv4, (_, _) => ValueTask.FromResult<IPAddress?>(IPAddress.Loopback)), Is.Null);
    }

    [Test]
    public async Task DiscoveryCancellationSkipsOrStopsWaitingForLookup()
    {
        await using ServiceProvider services = CreateServices();
        await using ILocalPeer local = CreatePeer(services);
        PublicAddressDiscovery discovery = local.GetPublicAddressDiscovery()!;

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await discovery.GetCandidateAsync(IPv4,
            (_, _) => throw new InvalidOperationException("A canceled query must not start a lookup"), canceled.Token));

        using CancellationTokenSource running = new();
        TaskCompletionSource<IPAddress?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lookupToken = default;
        Task<IPAddress?> query = discovery.GetCandidateAsync(IPv4, (_, token) =>
        {
            lookupToken = token;
            return new(pending.Task);
        }, running.Token).AsTask();

        try
        {
            Assert.That(lookupToken, Is.EqualTo(running.Token));
            running.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await query.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            pending.TrySetResult(FirstCandidate);
        }
    }

    private static ServiceProvider CreateServices(TimeProvider? clock = null)
    {
        IServiceCollection services = new ServiceCollection();
        if (clock is not null) services.AddSingleton(clock);
        return services.AddLibp2p(builder => builder.WithPublicAddressDiscovery()).BuildServiceProvider();
    }

    private static ILocalPeer CreatePeer(IServiceProvider services) =>
        services.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));

    private static ByteString Encode(string address) => ByteString.CopyFrom(Multiaddress.Decode(address).ToBytes());

    private static async Task<IdentifyResult> ReportAsync(ILocalPeer local, int reporter, string endpoint, ByteString observation,
        Identity? messageIdentity = null)
    {
        Identity identity = TestPeers.Identity(reporter);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.Peer.Returns(local);
        context.State.Returns(new State
        {
            RemoteAddress = $"{endpoint}/p2p/{identity.PeerId}",
            RemotePublicKey = identity.PublicKey,
            IsRemoteIdentityAuthenticated = true
        });

        Channel channel = new();
        Task write = channel.Reverse.WriteSizeAndProtobufAsync(new Identify
        {
            PublicKey = (messageIdentity ?? identity).PublicKey.ToByteString(),
            ObservedAddr = observation
        }).AsTask();
        try
        {
            return await new IdentifyProtocol(new ProtocolStackSettings()).DialAsync(channel, context);
        }
        finally
        {
            await channel.CloseAsync();
            await write;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class ObservedAddressBuilder(IServiceProvider services) : Libp2pPeerFactoryBuilder(services)
    {
        protected override ProtocolRef[] Encryption() => [new(new TestEndpoints(ServiceProvider.GetRequiredService<IConnectionProtocol>()))];
    }

    // Substitute the two sides' view of the socket endpoints while keeping the ordinary
    // TCP transport, security handshake, Identify exchange and automatic session initialization.
    private sealed class TestEndpoints(IConnectionProtocol inner) : IConnectionProtocol
    {
        public string Id => inner.Id;

        public Task DialAsync(IChannel channel, IConnectionContext context)
        {
            int host = context.State.RemotePeerId == TestPeers.PeerId(2) ? 2 : 3;
            context.State.RemoteAddress = $"/ip4/11.1.1.{host}/tcp/4001/p2p/{context.State.RemotePeerId}";
            return inner.DialAsync(channel, context);
        }

        public Task ListenAsync(IChannel channel, IConnectionContext context)
        {
            int port = context.Peer.Identity.PeerId == TestPeers.PeerId(2) ? 54321 : 54322;
            context.State.RemoteAddress = $"/ip4/8.8.4.4/tcp/{port}";
            return inner.ListenAsync(channel, context);
        }
    }
}
