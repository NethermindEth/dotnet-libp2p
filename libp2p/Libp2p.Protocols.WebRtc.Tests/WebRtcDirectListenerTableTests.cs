// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// The table's clock is injected and nothing here touches a socket, so every TTL, cap and eviction rule is
/// exercised at fake-clock speed. There is no <c>Task.Delay</c> and no <c>await</c> in this file at all.
/// </summary>
[TestFixture]
public class WebRtcDirectListenerTableTests
{
    private sealed record Session(string Name);

    /// <summary>A DTLS 1.2 handshake record header: first byte 22, inside RFC 7983's 20..63 DTLS range.</summary>
    private static byte[] DtlsRecord => [0x16, 0xFE, 0xFD, 0x00, 0x00, 0x00, 0x00, 0x00];

    private long _clock;

    [SetUp]
    public void SetUp() => _clock = 1_000;

    /// <summary>
    /// The real limiter with its production caps would reject the floods below long before the table's own
    /// caps came into play, and it is covered by its own fixture. Raising its bounds here isolates the
    /// admission rules under test; the limiter's place in the admission path is pinned separately by
    /// <see cref="Route_RefusesAdmissionWhenTheRateLimiterSaysNo"/>.
    /// </summary>
    private WebRtcDirectListenerTable<Session> NewTable() =>
        new(() => _clock, new WebRtcDirectRateLimiter(
            maxEndpointBuckets: 1 << 20,
            endpointBurst: 1 << 24,
            globalMessageBurst: 1 << 24,
            globalByteBurst: 1 << 30,
            nowMs: () => _clock));

    /// <summary>
    /// A v2 server ufrag whose client password half is the 22 digits the spec's length bound demands.
    /// Digits are ice-chars (RFC 8839 section 5.4), so this is a legal ufrag and not just a legal string.
    /// </summary>
    private static string V2Ufrag(int index) => WebRtcIceCredentials.BuildV2Ufrag(index.ToString("D22"));

    private static string V1Ufrag(int index) => WebRtcIceCredentials.V1Prefix + index.ToString("D8");

    /// <summary>
    /// Builds a genuine signed binding request the same way <see cref="WebRtcDirectStun.BuildBindingSuccessResponse"/>
    /// signs its reply: SIPSorcery's own serialiser, MESSAGE-INTEGRITY keyed with the string and a
    /// FINGERPRINT appended. <paramref name="signingKey"/> defaults to the server ufrag, which is what a
    /// real peer uses; passing anything else forges the HMAC.
    /// </summary>
    private static byte[] BindingRequest(string serverUfrag, string clientUfrag = "PSAY", string? signingKey = null)
    {
        STUNMessage request = new(STUNMessageTypesEnum.BindingRequest);
        request.AddUsernameAttribute($"{serverUfrag}:{clientUfrag}");
        return request.ToByteBufferStringKey(signingKey ?? serverUfrag, true);
    }

    private static IPEndPoint Peer(int port = 40_000, string address = "203.0.113.7") => new(IPAddress.Parse(address), port);

    private WebRtcDirectRoute<Session> Establish(
        WebRtcDirectListenerTable<Session> table, string ufrag, IPEndPoint source, Session session)
    {
        WebRtcDirectRoute<Session> admitted = table.Route(source, BindingRequest(ufrag));
        Assume.That(admitted.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
        Assume.That(table.MarkEstablished(admitted.Key, session), Is.True);
        return admitted;
    }

    /// <summary>
    /// The one that would have caught a fatal flaw in an address-map-first design. SIPSorcery keeps an ICE
    /// <c>_connectivityChecksTimer</c> running at <c>CONNECTED_CHECK_PERIOD</c> = 3s after a pair is
    /// nominated, so an established peer's own, already-known source address keeps producing STUN for the
    /// life of the connection. There is no ICE agent behind our session to answer them, so routing them to
    /// the session instead of answering them passes a 2s loopback test and then trips the same stack's
    /// <c>FAILED_TIMEOUT_PERIOD</c> = 16s and drops every real dialer.
    /// </summary>
    [Test]
    public void Route_AnswersAnEstablishedPeersConsentCheck_AndNeverDeliversItToTheSession()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint peer = Peer();
        string ufrag = V2Ufrag(1);
        Session session = new("established");

        WebRtcDirectRoute<Session> admitted = Establish(table, ufrag, peer, session);

        // The address really is known for DTLS, so the address-map hit this test is about does exist.
        Assert.That(table.Route(peer, DtlsRecord).Action, Is.EqualTo(WebRtcDirectRouteAction.DeliverToSession));

        for (int consentCheck = 0; consentCheck < 10; consentCheck++)
        {
            _clock += 5_000;
            WebRtcDirectRoute<Session> consent = table.Route(peer, BindingRequest(ufrag));

            Assert.Multiple(() =>
            {
                Assert.That(consent.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStun), $"check {consentCheck}");
                Assert.That(consent.Key, Is.EqualTo(admitted.Key));
                Assert.That(consent.Response, Is.Not.Null.And.Not.Empty);
                Assert.That(consent.Session, Is.Null);
            });
        }

        Assert.That(table.EstablishedCount, Is.EqualTo(1));
    }

    [Test]
    public void Route_AttachesThirtyTwoAddressesToOneUfrag_AndStillAnswersTheThirtyThird()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        string ufrag = V2Ufrag(2);
        Session session = new("multi-homed");

        // The admitting request attaches the first address, so the loop below adds the other 31.
        IPEndPoint[] addresses = [.. Enumerable.Range(0, WebRtcDirectListenerTable<Session>.MaxAddressesPerUfrag + 1)
            .Select(i => Peer(40_000 + i))];

        Establish(table, ufrag, addresses[0], session);

        for (int i = 1; i < addresses.Length; i++)
        {
            Assert.That(table.Route(addresses[i], BindingRequest(ufrag)).Action,
                Is.EqualTo(WebRtcDirectRouteAction.AnswerStun), $"address {i} must still be answered");
        }

        Assert.Multiple(() =>
        {
            for (int i = 0; i < WebRtcDirectListenerTable<Session>.MaxAddressesPerUfrag; i++)
            {
                Assert.That(table.Route(addresses[i], DtlsRecord).Action,
                    Is.EqualTo(WebRtcDirectRouteAction.DeliverToSession), $"address {i} should be attached");
            }

            // The 33rd was answered above but never written down, so DTLS from it has nowhere to go.
            Assert.That(table.Route(addresses[^1], DtlsRecord).Action, Is.EqualTo(WebRtcDirectRouteAction.Drop));
        });
    }

    [Test]
    public void Route_AdmitsBothFlows_AndAdmitsNothingForAnUnrecognisedPrefix()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();

        WebRtcDirectRoute<Session> v2 = table.Route(Peer(40_001), BindingRequest(V2Ufrag(3)));
        WebRtcDirectRoute<Session> v1 = table.Route(Peer(40_002), BindingRequest(V1Ufrag(4)));

        // Ice-chars throughout and a well-formed USERNAME: only the prefix is wrong, so this is the
        // version check failing and nothing else.
        WebRtcDirectRoute<Session> unknown = table.Route(Peer(40_003), BindingRequest("libp2p+webrtc+v9/ABCDEFGH"));
        WebRtcDirectRoute<Session> bare = table.Route(Peer(40_004), BindingRequest("ABCDEFGHIJKLMNOP"));

        Assert.Multiple(() =>
        {
            Assert.That(v2.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
            Assert.That(v1.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
            Assert.That(unknown.Action, Is.EqualTo(WebRtcDirectRouteAction.Drop));
            Assert.That(unknown.Response, Is.Null);
            Assert.That(bare.Action, Is.EqualTo(WebRtcDirectRouteAction.Drop));
            Assert.That(bare.Response, Is.Null);
            Assert.That(table.PendingCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Route_DropsAndThrowsNothingForJunk()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        string ufrag = V2Ufrag(5);
        IPEndPoint peer = Peer(40_005);

        byte[] twentyRandomBytes = new byte[20];
        new Random(20_250_101).NextBytes(twentyRandomBytes);

        // A success response, which parses as STUN but is not a binding request. Taken from the table's own
        // answer so it is a genuine, correctly signed one rather than a hand-rolled approximation.
        byte[] successResponse = table.Route(peer, BindingRequest(ufrag)).Response!;
        Assume.That(successResponse, Is.Not.Null);
        table.Remove(new WebRtcDirectSessionKey(ufrag, false));

        (string Name, byte[] Datagram)[] junk =
        [
            ("bad-hmac", BindingRequest(V2Ufrag(6), signingKey: "wrongkey")),
            ("success-response", successResponse),
            ("twenty-random-bytes", twentyRandomBytes),
            ("empty", []),
        ];

        Assert.Multiple(() =>
        {
            foreach ((string name, byte[] datagram) in junk)
            {
                Assert.That(() => table.Route(peer, datagram), Throws.Nothing, name);
                Assert.That(table.Route(peer, datagram).Action, Is.EqualTo(WebRtcDirectRouteAction.Drop), name);
            }

            Assert.That(table.PendingCount, Is.Zero);
            Assert.That(table.EstablishedCount, Is.Zero);
        });
    }

    [Test]
    public void Route_RefusesAdmissionWhenTheRateLimiterSaysNo()
    {
        // A limiter that accepts exactly one message, to prove admission really is behind it.
        WebRtcDirectListenerTable<Session> table = new(
            () => _clock,
            new WebRtcDirectRateLimiter(globalMessageBurst: 1, globalMessageRatePerSecond: 0, nowMs: () => _clock));

        Assert.Multiple(() =>
        {
            Assert.That(table.Route(Peer(40_006), BindingRequest(V2Ufrag(7))).Action,
                Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
            Assert.That(table.Route(Peer(40_007), BindingRequest(V2Ufrag(8))).Action,
                Is.EqualTo(WebRtcDirectRouteAction.Drop));
            Assert.That(table.PendingCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Route_CapsPendingRows_EvictingTheOldestFirst()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        const int floodSize = 2_000;

        for (int i = 0; i < floodSize; i++)
        {
            _clock += 1;
            Assume.That(table.Route(Peer(20_000 + i), BindingRequest(V2Ufrag(i))).Action,
                Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
        }

        Assert.That(table.PendingCount, Is.EqualTo(WebRtcDirectListenerTable<Session>.MaxPendingSessions));
        Assert.That(table.EstablishedCount, Is.Zero);

        // The newest survivor is still known; the oldest is not, so it is admitted afresh.
        Assert.That(table.Route(Peer(20_000 + floodSize - 1), BindingRequest(V2Ufrag(floodSize - 1))).Action,
            Is.EqualTo(WebRtcDirectRouteAction.AnswerStun));
        Assert.That(table.Route(Peer(20_000), BindingRequest(V2Ufrag(0))).Action,
            Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
    }

    /// <summary>
    /// The F4 invariant, and the most important test in the file. Every forged request below carries a
    /// VALID MESSAGE-INTEGRITY — the key is the server ufrag, which the attacker invents and puts in the
    /// same packet, so the HMAC costs them nothing — and every one arrives from the established peer's own
    /// source address, which is the hijack an address map that could be re-pointed would hand over. After
    /// ten thousand of them the session must still be reachable by BOTH keys.
    /// </summary>
    [Test]
    public void Route_LeavesAnEstablishedSessionUntouchedInBothMapsUnderAForgedUfragFlood()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint peer = Peer(40_008);
        string ufrag = V2Ufrag(9);
        Session session = new("victim");

        WebRtcDirectRoute<Session> admitted = Establish(table, ufrag, peer, session);
        Assume.That(table.Route(peer, DtlsRecord).Egress, Is.EqualTo(peer));

        for (int i = 0; i < 10_000; i++)
        {
            _clock += 1;
            table.Route(peer, BindingRequest(V2Ufrag(1_000_000 + i)));
        }

        WebRtcDirectRoute<Session> consent = table.Route(peer, BindingRequest(ufrag));
        WebRtcDirectRoute<Session> delivered = table.Route(peer, DtlsRecord);

        Assert.Multiple(() =>
        {
            // Primary map: (server_ufrag, isIPv6) still resolves, and still to a live row.
            Assert.That(consent.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStun));
            Assert.That(consent.Key, Is.EqualTo(admitted.Key));

            // Secondary map: the address still resolves to the same session, with its egress unmoved.
            Assert.That(delivered.Action, Is.EqualTo(WebRtcDirectRouteAction.DeliverToSession));
            Assert.That(delivered.Session, Is.SameAs(session));
            Assert.That(delivered.Egress, Is.EqualTo(peer));

            Assert.That(table.EstablishedCount, Is.EqualTo(1));
            Assert.That(table.PendingCount,
                Is.LessThanOrEqualTo(WebRtcDirectListenerTable<Session>.MaxPendingSessions));
        });
    }

    [Test]
    public void Reap_DropsAStunOnlyRowAtFifteenSecondsAndAnEstablishedOneAtFiveMinutes_NeitherBefore()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        long admittedAt = _clock;
        IPEndPoint pendingPeer = Peer(40_009);
        IPEndPoint establishedPeer = Peer(40_010);
        Session session = new("long-lived");

        Assume.That(table.Route(pendingPeer, BindingRequest(V2Ufrag(10))).Action,
            Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
        Establish(table, V2Ufrag(11), establishedPeer, session);

        _clock = admittedAt + WebRtcDirectListenerTable<Session>.StunOnlyIdleMs - 1;
        Assert.Multiple(() =>
        {
            Assert.That(table.Reap(), Is.Empty);
            Assert.That(table.PendingCount, Is.EqualTo(1), "a STUN-only row survives up to its idle bound");
            Assert.That(table.EstablishedCount, Is.EqualTo(1));
        });

        _clock = admittedAt + WebRtcDirectListenerTable<Session>.StunOnlyIdleMs;
        Assert.Multiple(() =>
        {
            // Nothing is returned: a pending row owns no session, so there is nothing for the caller to close.
            Assert.That(table.Reap(), Is.Empty);
            Assert.That(table.PendingCount, Is.Zero);
            Assert.That(table.EstablishedCount, Is.EqualTo(1), "the established row is on a different budget");
        });

        _clock = admittedAt + WebRtcDirectListenerTable<Session>.EstablishedIdleMs - 1;
        Assert.Multiple(() =>
        {
            Assert.That(table.Reap(), Is.Empty);
            Assert.That(table.EstablishedCount, Is.EqualTo(1));
        });

        _clock = admittedAt + WebRtcDirectListenerTable<Session>.EstablishedIdleMs;
        List<Session> reaped = table.Reap();

        Assert.Multiple(() =>
        {
            Assert.That(reaped, Is.EqualTo(new[] { session }), "the caller must get the session back to close it");
            Assert.That(table.EstablishedCount, Is.Zero);

            // Both maps are cleared, not just the primary one.
            Assert.That(table.Route(establishedPeer, DtlsRecord).Action, Is.EqualTo(WebRtcDirectRouteAction.Drop));
            Assert.That(table.Route(establishedPeer, BindingRequest(V2Ufrag(11))).Action,
                Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
        });
    }

    [Test]
    public void MarkEstablished_NeverReplacesASessionAndStopsAtTheEstablishedCap()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint peer = Peer(40_011);
        string ufrag = V2Ufrag(12);
        Session first = new("first");

        WebRtcDirectRoute<Session> admitted = Establish(table, ufrag, peer, first);

        Assert.Multiple(() =>
        {
            Assert.That(table.MarkEstablished(admitted.Key, new Session("second")), Is.False);
            Assert.That(table.Route(peer, DtlsRecord).Session, Is.SameAs(first));
            Assert.That(table.MarkEstablished(new WebRtcDirectSessionKey(V2Ufrag(13), false), new Session("absent")),
                Is.False);
        });

        // Fill the established cap, then confirm admission itself stops rather than queueing work that
        // could never be promoted.
        for (int i = 1; i < WebRtcDirectListenerTable<Session>.MaxEstablishedSessions; i++)
        {
            WebRtcDirectRoute<Session> route = table.Route(Peer(50_000 + i), BindingRequest(V2Ufrag(2_000 + i)));
            Assume.That(route.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));
            Assume.That(table.MarkEstablished(route.Key, new Session($"filler-{i}")), Is.True);
        }

        Assert.Multiple(() =>
        {
            Assert.That(table.EstablishedCount, Is.EqualTo(WebRtcDirectListenerTable<Session>.MaxEstablishedSessions));
            Assert.That(table.Route(Peer(51_000), BindingRequest(V2Ufrag(3_000))).Action,
                Is.EqualTo(WebRtcDirectRouteAction.Drop));
        });
    }

    [Test]
    public void Route_PinsTheEgressToTheFirstDtlsRecordAndNeverMovesIt()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint first = Peer(40_012);
        IPEndPoint second = Peer(40_013);
        string ufrag = V2Ufrag(14);
        Session session = new("pinned");

        Establish(table, ufrag, first, session);
        Assume.That(table.Route(second, BindingRequest(ufrag)).Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStun));

        Assert.That(table.Route(first, DtlsRecord).Egress, Is.EqualTo(first));

        // The second address is legitimately attached and its records are delivered, but it does not get to
        // redirect where the association writes.
        WebRtcDirectRoute<Session> fromSecond = table.Route(second, DtlsRecord);

        Assert.Multiple(() =>
        {
            Assert.That(fromSecond.Action, Is.EqualTo(WebRtcDirectRouteAction.DeliverToSession));
            Assert.That(fromSecond.Egress, Is.EqualTo(first));
        });
    }

    [Test]
    public void Route_KeysTheSameUfragSeparatelyPerAddressFamily_AndNormalisesMappedSources()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        string ufrag = V2Ufrag(15);
        IPEndPoint v4 = new(IPAddress.Parse("203.0.113.7"), 40_014);
        IPEndPoint mappedV4 = new(IPAddress.Parse("::ffff:203.0.113.7"), 40_014);
        IPEndPoint v6 = new(IPAddress.Parse("2001:db8::1"), 40_014);

        WebRtcDirectRoute<Session> admittedV4 = table.Route(v4, BindingRequest(ufrag));

        Assert.Multiple(() =>
        {
            Assert.That(admittedV4.Key, Is.EqualTo(new WebRtcDirectSessionKey(ufrag, false)));

            // Same peer behind a dual-mode socket: one key, so this is the known row, not a second one.
            Assert.That(table.Route(mappedV4, BindingRequest(ufrag)).Action,
                Is.EqualTo(WebRtcDirectRouteAction.AnswerStun));

            // A genuinely different family is a different session.
            Assert.That(table.Route(v6, BindingRequest(ufrag)).Key,
                Is.EqualTo(new WebRtcDirectSessionKey(ufrag, true)));
            Assert.That(table.PendingCount, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// F4 under the sharpest input there is: a forged ufrag whose SOURCE is the victim's own address.
    /// Minting an integrity-valid binding request costs an attacker nothing — the key is the server ufrag,
    /// which they invent and put in the same packet — so if attaching a source address could take one off
    /// another row, a single spoofed packet would re-point a live association's secondary key. The
    /// attacker's ufrag is new, so it is admitted; what it must not get is the address.
    /// </summary>
    [Test]
    public void Route_AdmitsAForgedUfragFromAVictimsAddress_ButNeverTakesTheAddressFromIt()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint victimAddress = Peer(40_015);
        Session victim = new("victim");

        Establish(table, V2Ufrag(16), victimAddress, victim);
        Assume.That(table.Route(victimAddress, DtlsRecord).Egress, Is.EqualTo(victimAddress));

        WebRtcDirectRoute<Session> attacker = table.Route(victimAddress, BindingRequest(V2Ufrag(17)));
        Assume.That(attacker.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));

        WebRtcDirectRoute<Session> delivered = table.Route(victimAddress, DtlsRecord);

        Assert.Multiple(() =>
        {
            Assert.That(delivered.Action, Is.EqualTo(WebRtcDirectRouteAction.DeliverToSession));
            Assert.That(delivered.Session, Is.SameAs(victim));
            Assert.That(delivered.Egress, Is.EqualTo(victimAddress));

            // Nor may the attacker take it on the way out: tearing its row down must unmap only addresses
            // that row actually owns, and it owns none.
            Assert.That(table.Remove(attacker.Key), Is.True);
            Assert.That(table.Route(victimAddress, DtlsRecord).Session, Is.SameAs(victim));
        });
    }

    /// <summary>
    /// A pending row has no session to deliver to, so a DTLS record keyed to its address is dropped rather
    /// than handed on with a null session for the socket loop to dereference. Between AnswerStunAndAdmit
    /// and MarkEstablished the handshake the caller started owns those records; the table only begins
    /// routing them once it has somewhere to put them.
    /// </summary>
    [Test]
    public void Route_DropsDtlsForAPendingRow_AndDeliversItOnlyOnceEstablished()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint peer = Peer(40_016);
        Session session = new("late");

        WebRtcDirectRoute<Session> admitted = table.Route(peer, BindingRequest(V2Ufrag(18)));
        Assume.That(admitted.Action, Is.EqualTo(WebRtcDirectRouteAction.AnswerStunAndAdmit));

        WebRtcDirectRoute<Session> beforeSession = table.Route(peer, DtlsRecord);
        Assume.That(table.MarkEstablished(admitted.Key, session), Is.True);
        WebRtcDirectRoute<Session> afterSession = table.Route(peer, DtlsRecord);

        Assert.Multiple(() =>
        {
            Assert.That(beforeSession.Action, Is.EqualTo(WebRtcDirectRouteAction.Drop));
            Assert.That(beforeSession.Session, Is.Null);
            Assert.That(afterSession.Action, Is.EqualTo(WebRtcDirectRouteAction.DeliverToSession));
            Assert.That(afterSession.Session, Is.SameAs(session));
        });
    }

    /// <summary>
    /// <see cref="WebRtcDirectListenerTable{TSession}.Admit"/> is the same admission path with no datagram
    /// in hand. It must refuse a ufrag the table already knows rather than start a second row over the
    /// top of it, which would orphan the addresses and the session the first one owns.
    /// </summary>
    [Test]
    public void Admit_CreatesOnePendingRow_AndRefusesToReplaceAKnownOne()
    {
        WebRtcDirectListenerTable<Session> table = NewTable();
        IPEndPoint peer = Peer(40_017);
        string ufrag = V2Ufrag(19);
        Assume.That(
            WebRtcIceCredentials.TryParseStunUsername($"{ufrag}:PSAY", out WebRtcIceCredentials credentials),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(table.Admit(credentials, peer, 64), Is.True);
            Assert.That(table.PendingCount, Is.EqualTo(1));

            Assert.That(table.Admit(credentials, peer, 64), Is.False, "the ufrag is known now");
            Assert.That(table.PendingCount, Is.EqualTo(1));

            // The row it made is a real one in both maps: Route finds it rather than admitting again.
            Assert.That(table.Route(peer, BindingRequest(ufrag)).Action,
                Is.EqualTo(WebRtcDirectRouteAction.AnswerStun));
        });
    }

    /// <summary>
    /// Every other test in this file is written against the constants, which means every other test follows
    /// a constant that drifts: doubling any of them below leaves the whole fixture green. These five numbers
    /// are a design decision with arithmetic behind them, not an implementation detail, so here the numbers
    /// themselves are the oracle and changing one has to be deliberate.
    /// </summary>
    [Test]
    public void Caps_AndIdleBounds_AreTheNumbersTheDesignFixed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WebRtcDirectListenerTable<Session>.MaxPendingSessions, Is.EqualTo(1_024));
            Assert.That(WebRtcDirectListenerTable<Session>.MaxEstablishedSessions, Is.EqualTo(128));
            Assert.That(WebRtcDirectListenerTable<Session>.MaxAddressesPerUfrag, Is.EqualTo(32));
            Assert.That(WebRtcDirectListenerTable<Session>.StunOnlyIdleMs, Is.EqualTo(15_000));
            Assert.That(WebRtcDirectListenerTable<Session>.EstablishedIdleMs, Is.EqualTo(300_000));
        });
    }
}
