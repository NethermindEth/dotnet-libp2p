// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Relay;
using Nethermind.Libp2p.Protocols.Relay.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Libp2p.E2eTests;

/// <summary>
/// Circuit relay v2 E2e tests. These run in Libp2p.E2eTests (not Pubsub E2eTests);
/// any failure in Libp2p.Protocols.Pubsub.E2eTests is unrelated (e.g. flaky reconnection tests).
/// </summary>
public class RelayE2eTestSetup : E2eTestSetup
{
    protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        // Reuse the base stack (Identify, RequestResponse sample, etc.) and enable relay support.
        return base.ConfigureLibp2p(builder.WithRelay());
    }

    protected override Multiaddress[] GetListenAddresses(int index) =>
        [Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")];
}

public class RelayWebRtcE2eTestSetup : E2eTestSetup
{
    protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        return base.ConfigureLibp2p(builder
            .WithRelay()
            .WithWebRtc());
    }

    protected override Multiaddress[] GetListenAddresses(int index) =>
        [Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")];
}

[TestFixture]
public class RelayE2eTests
{
    [Test]
    public async Task StopConnect_MissingPeer_ReturnsMalformedMessageWithoutUpgrading()
    {
        RelayStopProtocol protocol = new();
        Channel channel = new();
        ISessionContext context = Substitute.For<ISessionContext>();

        Task listenTask = protocol.ListenAsync(channel, context);
        await channel.Reverse.WriteSizeAndProtobufAsync(new StopMessage
        {
            Type = StopMessage.Types.Type.Connect
        });

        StopMessage response = await channel.Reverse.ReadPrefixedProtobufAsync(StopMessage.Parser);
        await listenTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(response.Status, Is.EqualTo(Status.MalformedMessage));
        _ = context.DidNotReceive().CreateConnection();
    }

    [Test]
    public void RelayTransport_DoesNotMatchRelayedWebRtcAddress()
    {
        Multiaddress relayedWebRtc = "/ip4/127.0.0.1/tcp/4001/ws/p2p/12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF/p2p-circuit/webrtc/p2p/12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";
        Multiaddress plainCircuit = "/ip4/127.0.0.1/tcp/4001/ws/p2p/12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF/p2p-circuit/p2p/12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";

        Assert.That(RelayCircuitTransportProtocol.IsAddressMatch(relayedWebRtc), Is.False);
        Assert.That(RelayCircuitTransportProtocol.IsAddressMatch(plainCircuit), Is.True);
    }

    [Test]
    public async Task RelayOnlyStack_RejectsRelayedWebRtcAddressInsteadOfDialingUnderlyingWebSocket()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder
                .WithWebSockets()
                .WithRelay()
                .WithPlaintextEnforced()
                .AddProtocol<IncrementNumberTestProtocol>())
            .BuildServiceProvider();

        IPeerFactory peerFactory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer dialer = peerFactory.Create(TestPeers.Identity(1));
        Multiaddress relayedWebRtc = "/ip4/127.0.0.1/tcp/4001/ws/p2p/12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF/p2p-circuit/webrtc/p2p/12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";

        Libp2pSetupException? exception = Assert.ThrowsAsync<Libp2pSetupException>(async () =>
            await dialer.DialAsync(relayedWebRtc));

        Assert.That(exception?.Message, Does.Contain("WithWebRtc"));
    }

    [Test]
    public async Task RelayOnlyStack_MixedRelayedWebRtcCandidateDoesNotBlockDirectCandidate()
    {
        const int relayIndex = 0;
        const int initiatorIndex = 1;
        const int targetIndex = 2;

        await using RelayE2eTestSetup test = new();
        await test.AddPeersAsync(3);

        ILocalPeer relay = test.Peers[relayIndex];
        ILocalPeer initiator = test.Peers[initiatorIndex];
        ILocalPeer target = test.Peers[targetIndex];

        Multiaddress relayedWebRtcCandidate = Multiaddress.Decode($"{relay.ListenAddresses.First()}/p2p-circuit/webrtc/p2p/{target.Identity.PeerId}");
        Multiaddress directCandidate = target.ListenAddresses.First();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ISession session = await initiator.DialAsync([relayedWebRtcCandidate, directCandidate], timeout.Token);
        int response = await session.DialAsync<IncrementNumberTestProtocol, int, int>(41, timeout.Token);

        Assert.That(session.RemoteAddress.ToString(), Does.Not.Contain("/p2p-circuit/webrtc/"));
        Assert.That(response, Is.EqualTo(42));
    }

    [Test]
    public async Task CircuitRelay_ReserveAndConnect_Succeeds()
    {
        const int relayIndex = 0;
        const int initiatorIndex = 1;
        const int targetIndex = 2;

        await using RelayE2eTestSetup test = new();

        await test.AddPeersAsync(3);

        ILocalPeer relay = test.Peers[relayIndex];
        ILocalPeer initiator = test.Peers[initiatorIndex];
        ILocalPeer target = test.Peers[targetIndex];

        // Use first listen address of the relay for simplicity (all are localhost in tests).
        Multiaddress relayAddr = relay.ListenAddresses.First();

        // 1. Target reserves a slot at the relay (Hop RESERVE).
        ISession targetToRelaySession = await target.DialAsync(relayAddr);

        HopMessage reserveRequest = new()
        {
            Type = HopMessage.Types.Type.Reserve
        };

        HopMessage reserveResponse =
            await targetToRelaySession.DialAsync<RelayHopProtocol, HopMessage, HopMessage>(reserveRequest);

        Assert.That(reserveResponse.Status, Is.EqualTo(Status.Ok), "RESERVE should succeed");
        Assert.That(reserveResponse.Reservation, Is.Not.Null, "Reservation details should be present");
        Assert.That(reserveResponse.Reservation.Addrs.Count, Is.GreaterThan(0), "Relay should report at least one listen address");

        // Sanity-check that the reservation is visible in the relay's reservation store.
        IRelayReservationStore reservationStore =
            (IRelayReservationStore)test.ServiceProviders[relayIndex].GetService(typeof(IRelayReservationStore))!;

        ReservationEntry? entry = reservationStore.TryGet(target.Identity.PeerId);
        Assert.That(entry, Is.Not.Null, "Reservation entry should exist for target peer");

        // 2. Initiator asks the relay to CONNECT to the reserved target (Hop CONNECT).
        ISession initiatorToRelaySession = await initiator.DialAsync(relayAddr);

        HopMessage connectRequest = new()
        {
            Type = HopMessage.Types.Type.Connect,
            Peer = new Peer
            {
                Id = ByteString.CopyFrom(target.Identity.PeerId.Bytes)
            }
        };

        HopMessage connectResponse =
            await initiatorToRelaySession.DialAsync<RelayHopProtocol, HopMessage, HopMessage>(connectRequest);

        Assert.That(connectResponse.Status, Is.EqualTo(Status.Ok), "CONNECT should succeed once reservation exists");
    }

    [Test]
    public async Task CircuitRelay_TransportDial_CarriesApplicationProtocol()
    {
        const int relayIndex = 0;
        const int initiatorIndex = 1;
        const int targetIndex = 2;

        await using RelayE2eTestSetup test = new();
        await test.AddPeersAsync(3);

        ILocalPeer relay = test.Peers[relayIndex];
        ILocalPeer initiator = test.Peers[initiatorIndex];
        ILocalPeer target = test.Peers[targetIndex];

        Multiaddress relayAddr = relay.ListenAddresses.First();
        ISession targetToRelaySession = await target.DialAsync(relayAddr);

        HopMessage reserveResponse = await targetToRelaySession.DialAsync<RelayHopProtocol, HopMessage, HopMessage>(new HopMessage
        {
            Type = HopMessage.Types.Type.Reserve
        });
        Assert.That(reserveResponse.Status, Is.EqualTo(Status.Ok), "RESERVE should succeed");

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Multiaddress relayedAddr = Multiaddress.Decode($"{relayAddr}/p2p-circuit/p2p/{target.Identity.PeerId}");

        ISession relayedSession = await initiator.DialAsync(relayedAddr, timeout.Token);
        int response = await relayedSession.DialAsync<IncrementNumberTestProtocol, int, int>(41, timeout.Token);

        Assert.That(relayedSession.RemoteAddress.ToString(), Does.Contain("/p2p-circuit/"));
        Assert.That(response, Is.EqualTo(42));
    }

    [Test]
    public async Task CircuitRelay_DirectSessionDoesNotSatisfyRelayedDial()
    {
        const int relayIndex = 0;
        const int initiatorIndex = 1;
        const int targetIndex = 2;

        await using RelayE2eTestSetup test = new();
        await test.AddPeersAsync(3);

        ILocalPeer relay = test.Peers[relayIndex];
        ILocalPeer initiator = test.Peers[initiatorIndex];
        ILocalPeer target = test.Peers[targetIndex];

        ISession directSession = await initiator.DialAsync(target.ListenAddresses.First());
        Assert.That(await directSession.DialAsync<IncrementNumberTestProtocol, int, int>(41), Is.EqualTo(42));

        Multiaddress relayAddr = relay.ListenAddresses.First();
        ISession targetToRelaySession = await target.DialAsync(relayAddr);
        HopMessage reserveResponse = await targetToRelaySession.DialAsync<RelayHopProtocol, HopMessage, HopMessage>(new HopMessage
        {
            Type = HopMessage.Types.Type.Reserve
        });
        Assert.That(reserveResponse.Status, Is.EqualTo(Status.Ok), "RESERVE should succeed");

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Multiaddress relayedAddr = Multiaddress.Decode($"{relayAddr}/p2p-circuit/p2p/{target.Identity.PeerId}");
        ISession relayedSession = await initiator.DialAsync(relayedAddr, timeout.Token);
        int response = await relayedSession.DialAsync<IncrementNumberTestProtocol, int, int>(42, timeout.Token);

        Assert.That(relayedSession, Is.Not.SameAs(directSession));
        Assert.That(relayedSession.RemoteAddress.ToString(), Does.Contain("/p2p-circuit/"));
        Assert.That(response, Is.EqualTo(43));
    }

    [Test]
    public async Task CircuitRelay_PlainCircuitSessionDoesNotSatisfyRelayedWebRtcDial()
    {
        const int relayIndex = 0;
        const int initiatorIndex = 1;
        const int targetIndex = 2;

        await using RelayWebRtcE2eTestSetup test = new();
        await test.AddPeersAsync(3);

        ILocalPeer relay = test.Peers[relayIndex];
        ILocalPeer initiator = test.Peers[initiatorIndex];
        ILocalPeer target = test.Peers[targetIndex];

        Multiaddress relayAddr = relay.ListenAddresses.First();
        ISession targetToRelaySession = await target.DialAsync(relayAddr);
        HopMessage reserveResponse = await targetToRelaySession.DialAsync<RelayHopProtocol, HopMessage, HopMessage>(new HopMessage
        {
            Type = HopMessage.Types.Type.Reserve
        });
        Assert.That(reserveResponse.Status, Is.EqualTo(Status.Ok), "RESERVE should succeed");

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Multiaddress plainRelayedAddr = Multiaddress.Decode($"{relayAddr}/p2p-circuit/p2p/{target.Identity.PeerId}");
        ISession plainRelayedSession = await initiator.DialAsync(plainRelayedAddr, timeout.Token);
        Assert.That(await plainRelayedSession.DialAsync<IncrementNumberTestProtocol, int, int>(41, timeout.Token), Is.EqualTo(42));

        Multiaddress relayedWebRtcAddr = Multiaddress.Decode($"{relayAddr}/p2p-circuit/webrtc/p2p/{target.Identity.PeerId}");
        AggregateException? exception = Assert.ThrowsAsync<AggregateException>(async () =>
            await initiator.DialAsync(relayedWebRtcAddr, timeout.Token));

        Assert.That(exception?.Flatten().InnerExceptions, Has.Some.TypeOf<PlatformNotSupportedException>());
    }
}
