// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Relay;
using Nethermind.Libp2p.Protocols.Relay.Dto;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class RelayE2eTestSetup : E2eTestSetup
{
    protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        // Reuse the base stack (Identify, RequestResponse sample, etc.) and enable relay support.
        return base.ConfigureLibp2p(builder.WithRelay());
    }
}

[TestFixture]
public class RelayE2eTests
{
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
}

