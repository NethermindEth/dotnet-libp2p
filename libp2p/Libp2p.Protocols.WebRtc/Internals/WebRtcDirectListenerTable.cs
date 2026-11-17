// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>What the socket loop must do with a datagram. The payload rides in <see cref="WebRtcDirectRoute{TSession}"/>.</summary>
internal enum WebRtcDirectRouteAction
{
    /// <summary>
    /// Send nothing and allocate nothing. Every unresolvable datagram ends here: a listener that answered
    /// an unvalidated source address with so much as an error would be a reflection and amplification
    /// primitive, so there is no 401 and no error response of any kind anywhere in this class.
    /// </summary>
    Drop = 0,

    /// <summary>Send <see cref="WebRtcDirectRoute{TSession}.Response"/> back to the source of the request. No session was created.</summary>
    AnswerStun,

    /// <summary>
    /// As <see cref="AnswerStun"/>, and a pending row now exists under
    /// <see cref="WebRtcDirectRoute{TSession}.Key"/>. The caller starts the DTLS handshake and hands the
    /// resulting session to <see cref="WebRtcDirectListenerTable{TSession}.MarkEstablished"/>.
    /// </summary>
    AnswerStunAndAdmit,

    /// <summary>Feed the datagram to <see cref="WebRtcDirectRoute{TSession}.Session"/>; write its replies to <see cref="WebRtcDirectRoute{TSession}.Egress"/>.</summary>
    DeliverToSession,
}

/// <summary>
/// The primary table key. A listener may serve the same ufrag on a v4 and a v6 socket at once, and the two
/// are different peers with different sessions, so the address family is part of the identity rather than
/// something the lookup has to guess. Taken from the NORMALIZED source, so a dual-mode socket's
/// <c>::ffff:a.b.c.d</c> delivery keys as v4 exactly like the same peer arriving on a v4 socket.
/// </summary>
internal readonly record struct WebRtcDirectSessionKey(string ServerUfrag, bool IsIPv6);

/// <summary>
/// A routing decision. Inert and side-effect free once returned, so a test can assert on it and so the
/// socket loop can do its I/O outside the table's lock.
/// </summary>
internal readonly record struct WebRtcDirectRoute<TSession>(
    WebRtcDirectRouteAction Action,
    WebRtcDirectSessionKey Key,
    byte[]? Response,
    TSession? Session,
    IPEndPoint? Egress,
    TSession? EvictedSession = null)
    where TSession : class
{
    public static WebRtcDirectRoute<TSession> Drop => default;
}

/// <summary>
/// The listener's routing and admission brain for one webrtc-direct UDP port: which session a datagram
/// belongs to, whether an unknown peer is allowed to create one, and when a row is dead.
/// <para>
/// It owns no socket, no timer and no SIPSorcery object beyond the <see cref="STUNMessage"/> that
/// <see cref="WebRtcDirectStun"/> hands back, it is generic in the session type, and it takes its clock as
/// a parameter. That is the point of it being a separate class from the receive loop: every cap, TTL and
/// eviction rule below is reachable from a test at fake-clock speed, with no <c>Task.Delay</c> and no live
/// stack, which is the only way the flood-resistance rules get covered at all.
/// </para>
/// <para>
/// The hard invariant every method here is written to preserve: NO INBOUND PACKET MAY EVER EVICT OR
/// RE-POINT LIVE STATE. STUN MESSAGE-INTEGRITY is keyed with the server ufrag, which is the first half of
/// the USERNAME in the same packet (see <see cref="WebRtcDirectStun.CheckIntegrity"/>), so anyone who can
/// read one packet can mint integrity-valid ones for ever. A passing check rules out blind off-path
/// spoofing and authorizes nothing else. The only eviction anywhere in this class is oldest-first among
/// PENDING rows, which own no session; an established session is never evicted by anything off the wire.
/// </para>
/// </summary>
internal sealed class WebRtcDirectListenerTable<TSession>
    where TSession : class
{
    /// <summary>
    /// STUN-only rows: a key, two timestamps and a short address list, with no DTLS object and no threads,
    /// so ~2KB each and 1024 of them is ~2MB of worst-case flood residue. Passing an integrity check costs
    /// an attacker nothing (see the class remarks), so this bound — not the check — is what stands between
    /// a packet flood and unbounded allocation.
    /// </summary>
    public const int MaxPendingSessions = 1024;

    /// <summary>
    /// Established sessions. Each DTLS/SCTP association costs a dedicated receive thread, a dedicated send
    /// thread and a 256KB buffer, so 128 * (2 threads + 256KB) is ~256 threads and ~32MB: the most a single
    /// listen port should ever be asked to carry.
    /// </summary>
    public const int MaxEstablishedSessions = 128;

    /// <summary>
    /// A browser legitimately probes from several source ports at once, one per local ICE candidate, so one
    /// ufrag owning several source addresses is ordinary traffic and not an attack. 32 covers a multi-homed
    /// host with room to spare; past it we keep answering and merely stop writing bookkeeping down, because
    /// the peer is legitimate and it is only our own table that needs bounding.
    /// </summary>
    public const int MaxAddressesPerUfrag = 32;

    /// <summary>
    /// RFC 8489 section 6.2.1 retransmits a binding request on a 500ms initial RTO, so a row that has gone
    /// 15s without one has missed the entire retransmission schedule several times over and its peer is gone.
    /// </summary>
    public const long StunOnlyIdleMs = 15_000;

    /// <summary>
    /// An established peer keeps sending binding requests for the life of the connection: SIPSorcery 10.0.14
    /// leaves <c>RtpIceChannel._connectivityChecksTimer</c> running once a pair is nominated, on
    /// <c>CONNECTED_CHECK_PERIOD</c> = 3s, and abandons the pair at <c>DISCONNECTED_TIMEOUT_PERIOD</c> = 8s
    /// and <c>FAILED_TIMEOUT_PERIOD</c> = 16s. 300s of total silence is ~100 missed checks, long past the
    /// point where the peer's own stack has torn the association down.
    /// </summary>
    public const long EstablishedIdleMs = 300_000;

    private readonly Func<long> _nowMs;
    private readonly WebRtcDirectRateLimiter _rateLimiter;

    // ponytail: one lock over all three structures. Every operation is a handful of dictionary hits and the
    // caller does its I/O outside it, so contention needs a flood to even show up; shard by ufrag hash if a
    // profile ever disagrees.
    private readonly object _gate = new();

    /// <summary>Primary index, F2: (server_ufrag, isIPv6) -> row.</summary>
    private readonly Dictionary<WebRtcDirectSessionKey, Row> _byUfrag = [];

    /// <summary>
    /// Secondary index: normalized source address -> row, for DTLS records, which carry no ufrag. The third
    /// index the design calls for — key -> addresses, for cleanup and for the per-ufrag cap — lives on the
    /// row as <see cref="Row.Addresses"/>, which is the same mapping without a dictionary to keep in step.
    /// </summary>
    private readonly Dictionary<IPEndPoint, Row> _byAddress = [];

    private int _pendingCount;
    private int _establishedCount;

    /// <summary>
    /// Stamped on each row at admission so eviction can pick the oldest PENDING one. A counter rather than
    /// the clock because one millisecond holds a whole burst of admissions and the eviction order has to be
    /// total; it is also the only ordering an attacker cannot perturb by sending more packets, since a
    /// binding request from a known peer moves <see cref="Row.LastSeenMs"/> and nothing else.
    /// </summary>
    private long _nextAdmissionSeq;

    public WebRtcDirectListenerTable(Func<long>? nowMs = null, WebRtcDirectRateLimiter? rateLimiter = null)
    {
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _rateLimiter = rateLimiter ?? new WebRtcDirectRateLimiter(nowMs: _nowMs);
    }

    /// <summary>Rows admitted with no session attached yet. Bounded by <see cref="MaxPendingSessions"/>.</summary>
    public int PendingCount
    {
        get { lock (_gate) { return _pendingCount; } }
    }

    /// <summary>Rows carrying a session. Bounded by <see cref="MaxEstablishedSessions"/>.</summary>
    public int EstablishedCount
    {
        get { lock (_gate) { return _establishedCount; } }
    }

    /// <summary>
    /// Classifies and routes one datagram.
    /// <para>
    /// The order is fixed and it matters: the FIRST BYTE is classified per RFC 7983 BEFORE any table lookup.
    /// go-libp2p consults its address map first and we must not copy that — go is safe there only by
    /// accident of its stack, because an address-map hit is pushed into a pion muxedConnection whose own ICE
    /// agent answers the binding request. We have no ICE agent behind the map, and SIPSorcery's dialer
    /// re-sends binding requests for the LIFE of the connection: <c>RtpIceChannel</c> keeps
    /// <c>_connectivityChecksTimer</c> running at <c>CONNECTED_CHECK_PERIOD</c> = 3s after the pair is
    /// nominated and gives up at <c>FAILED_TIMEOUT_PERIOD</c> = 16s of silence. An established peer's check
    /// routed into DTLS instead of answered therefore looks perfectly fine for the two seconds of a loopback
    /// test and then drops every real dialer inside twenty. Binding requests are answered for ever,
    /// established or not.
    /// </para>
    /// </summary>
    public WebRtcDirectRoute<TSession> Route(IPEndPoint source, byte[] datagram)
    {
        // RFC 7983 first, table second.
        if (WebRtcDirectStun.IsStunDatagram(datagram))
        {
            return RouteStun(WebRtcDirectStun.Normalize(source), datagram);
        }

        if (WebRtcDirectStun.IsDtlsRecord(datagram))
        {
            return RouteDtls(WebRtcDirectStun.Normalize(source));
        }

        // SRTP, ZRTP, TURN channel data, garbage: nothing a webrtc-direct listener speaks.
        return WebRtcDirectRoute<TSession>.Drop;
    }

    private WebRtcDirectRoute<TSession> RouteStun(IPEndPoint source, byte[] datagram)
    {
        if (!WebRtcDirectStun.TryParseBindingRequest(datagram, out STUNMessage? request, out WebRtcIceCredentials credentials))
        {
            return WebRtcDirectRoute<TSession>.Drop;
        }

        if (!WebRtcDirectStun.CheckIntegrity(request, credentials.ServerUfrag))
        {
            return WebRtcDirectRoute<TSession>.Drop;
        }

        WebRtcDirectSessionKey key = KeyFor(credentials.ServerUfrag, source);
        bool nominates = WebRtcDirectStun.NominatesCandidate(request);
        TSession? evictedSession = null;

        lock (_gate)
        {
            if (_byUfrag.TryGetValue(key, out Row? row))
            {
                // An address the row already owns is never throttled. SIPSorcery re-checks every
                // RtpIceChannel.CONNECTED_CHECK_PERIOD (3s) and abandons the pair at
                // FAILED_TIMEOUT_PERIOD (16s), so throttling a consent check does not slow a peer down,
                // it disconnects one. A source the row does NOT own is unproven: minting an
                // integrity-valid request costs an attacker nothing (the key is in the packet), so
                // answering one unthrottled is a free reflection. The response is smaller than the
                // request, so this was never an amplifier -- the limiter just bounds the nuisance.
                bool known = row.Addresses.Contains(source);
                if (!known && !_rateLimiter.TryAccept(source, datagram.Length, out _))
                {
                    return WebRtcDirectRoute<TSession>.Drop;
                }

                row.LastSeenMs = _nowMs();
                AttachAddress(row, source);

                if (nominates)
                {
                    row.TryNominateEgress(source);
                }

                return Answer(WebRtcDirectRouteAction.AnswerStun, key, request, source);
            }

            if (!TryAdmitLocked(key, source, datagram.Length, out evictedSession))
            {
                return WebRtcDirectRoute<TSession>.Drop;
            }

            // The admitting request may itself be the nomination. Recording it here is what makes
            // that pair stick: without it nothing marked the row as nominated, so a second
            // nomination -- which anyone who has learned the ufrag can send -- replaced the first.
            if (nominates && _byUfrag.TryGetValue(key, out Row? admittedRow))
            {
                admittedRow.TryNominateEgress(source);
            }
        }

        return Answer(WebRtcDirectRouteAction.AnswerStunAndAdmit, key, request, source) with { EvictedSession = evictedSession };
    }

    private WebRtcDirectRoute<TSession> RouteDtls(IPEndPoint source)
    {
        lock (_gate)
        {
            // A DTLS record carries no ufrag, so the normalized source address is the only key available.
            // A pending row has nothing to deliver to, so it drops here too.
            if (!_byAddress.TryGetValue(source, out Row? row) || row.Session is null)
            {
                return WebRtcDirectRoute<TSession>.Drop;
            }

            row.LastSeenMs = _nowMs();

            // Only a record from the selected pair freezes the selection, because only such a
            // record will be admitted by the session. Any attached endpoint can put one byte in the
            // DTLS range on the wire; freezing on that let an unselected sender -- whose record the
            // session then refused -- block a genuine nomination that had not arrived yet.
            if (source.Equals(row.Egress))
            {
                row.MarkDtlsRouted();
            }

            // Records arriving from any of the ufrag's attached addresses are delivered -- DTLS
            // authenticates them itself, and the session checks the observed source separately --
            // but they are answered to Row.Egress, which by this point is either the admitting
            // address or the pair the dialer nominated, and can no longer move.
            return new(WebRtcDirectRouteAction.DeliverToSession, row.Key, null, row.Session, row.Egress);
        }
    }

    /// <summary>
    /// Creates a pending row for an as-yet-unknown ufrag, behind the rate limiter and both session caps.
    /// Public because <see cref="Route"/> is not the only way a listener may learn of a peer; the admission
    /// rules are the same either way. Returns false if the row already exists or admission was refused, and
    /// a refusal answers nothing.
    /// </summary>
    public bool Admit(WebRtcIceCredentials credentials, IPEndPoint source, int datagramBytes = 1) =>
        Admit(credentials, source, out _, datagramBytes);

    /// <summary>
    /// As above, also reporting any session the admission evicted so the caller can dispose it. A
    /// pending row can own a half-finished session, so eviction is no longer free.
    /// </summary>
    public bool Admit(WebRtcIceCredentials credentials, IPEndPoint source, out TSession? evicted, int datagramBytes = 1)
    {
        IPEndPoint normalized = WebRtcDirectStun.Normalize(source);
        WebRtcDirectSessionKey key = KeyFor(credentials.ServerUfrag, normalized);

        lock (_gate)
        {
            if (_byUfrag.ContainsKey(key))
            {
                evicted = null;
                return false;
            }

            return TryAdmitLocked(key, normalized, datagramBytes, out evicted);
        }
    }

    private bool TryAdmitLocked(WebRtcDirectSessionKey key, IPEndPoint source, int datagramBytes, out TSession? evicted)
    {
        evicted = null;
        if (!_rateLimiter.TryAccept(source, datagramBytes, out _))
        {
            return false;
        }

        // No point starting a handshake there is no room to finish, and refusing here keeps a flood from
        // parking pending rows that could never be promoted.
        if (_establishedCount >= MaxEstablishedSessions)
        {
            return false;
        }

        if (_pendingCount >= MaxPendingSessions && !TryEvictOldestPendingLocked(out evicted))
        {
            return false;
        }

        Row row = new(key, _nowMs(), _nextAdmissionSeq++, source);
        _byUfrag[key] = row;
        _pendingCount++;
        AttachAddress(row, source);
        return true;
    }

    /// <summary>
    /// Evicts the pending row admitted longest ago, and only ever a pending row: that one condition is the
    /// whole of the never-evict invariant.
    /// </summary>
    // ponytail: evicting a PENDING row lets its ufrag be re-admitted from a different address, which
    // re-pins the egress. The consequence is denial of one in-flight dial, not a hijack: eviction also
    // unmaps the honest peer's address, so its next DTLS record is dropped rather than delivered anywhere,
    // and an attacker re-admitting the ufrag only gets a fresh DTLS handshake of its own -- it cannot
    // continue the honest peer's, because the libp2p identity is bound at Noise by a prologue carrying both
    // certificate fingerprints, not by this table. The marginal harm is also small: an attacker able to
    // fill MaxPendingSessions is already denying every new dial, targeted or not.
    // Three narrower rules were considered and rejected. Refusing to evict a row that has received a DTLS
    // record, and tombstoning evicted ufrags, are both driven by attacker-supplied packets, so each
    // converts a soft cap into a hard one that an attacker can pin shut -- strictly worse. Binding the row
    // to the DTLS-authenticated certhash is already what Noise does, one layer up.
    // The real fix is return-routability before any state is allocated: a DTLS HelloVerifyRequest cookie
    // gate, which SIPSorcery 10.0.14 does not expose through DtlsSrtpTransport (see WebRtcDirectSession).
    // Raised by automated security review 2026-10-23; recorded rather than papered over.
    // ponytail: a linear scan of the table per eviction, where an admission-ordered index would be O(1).
    // It only runs with the pending cap already full, both caps bound it at MaxPendingSessions +
    // MaxEstablishedSessions rows, and the rate limiter in front of admission caps how often a flood can
    // reach it, so it is ~1150 reference comparisons on a path that is already rate-limited. An index is
    // what the first attempt used and it has to be kept in step with promotion, removal and reaping —
    // which it was not, so every Row it had ever seen stayed reachable from it for the life of the
    // listener. Reinstate one only with a profile saying the scan matters.
    private bool TryEvictOldestPendingLocked(out TSession? evicted)
    {
        evicted = null;
        Row? oldest = null;

        foreach (Row row in _byUfrag.Values)
        {
            if (!row.IsEstablished && (oldest is null || row.AdmissionSeq < oldest.AdmissionSeq))
            {
                oldest = row;
            }
        }

        if (oldest is null)
        {
            return false;
        }

        // A pending row can now own a session: attaching one no longer promotes the row, so the
        // oldest-pending victim may be mid-handshake and holding a DTLS stack and a parked thread.
        // Hand it back; the table owns no disposables and silently dropping it would leak both.
        evicted = oldest.Session;
        RemoveLocked(oldest);
        return true;
    }

    /// <summary>
    /// Attaches a session to a pending row, promoting it. Returns false — leaving the caller to tear the
    /// session down — if the row is gone, if it already carries a session, or if the established cap is
    /// full. It never replaces an existing session: that would be exactly the re-pointing the class forbids.
    /// </summary>
    public bool AttachSession(WebRtcDirectSessionKey key, TSession session)
    {
        lock (_gate)
        {
            if (!_byUfrag.TryGetValue(key, out Row? row) || row.Session is not null)
            {
                return false;
            }

            // Deliberately does NOT touch the counters. The row stays pending: still inside
            // MaxPendingSessions, still evictable oldest-first, still on StunOnlyIdleMs. Nothing a peer
            // can send without completing DTLS may move it out of the cheap pool.
            row.Session = session;
            row.LastSeenMs = _nowMs();
            return true;
        }
    }

    /// <summary>
    /// Promotes a row whose DTLS handshake has completed. Returns false -- leaving the caller to tear the
    /// session down -- if the row is gone, has no session attached, is already established, or the
    /// established cap is full. The cap is checked HERE rather than at attach, so a flood of unfinished
    /// handshakes cannot exhaust the capacity reserved for peers that actually completed one.
    /// </summary>
    public bool MarkEstablished(WebRtcDirectSessionKey key, TSession session)
    {
        lock (_gate)
        {
            if (!_byUfrag.TryGetValue(key, out Row? row) ||
                !ReferenceEquals(row.Session, session) ||
                row.IsEstablished ||
                _establishedCount >= MaxEstablishedSessions)
            {
                return false;
            }

            row.IsEstablished = true;
            row.LastSeenMs = _nowMs();
            _pendingCount--;
            _establishedCount++;
            return true;
        }
    }

    /// <summary>Forgets a row and every address keyed to it. The caller owns disposing the session.</summary>
    public bool Remove(WebRtcDirectSessionKey key, TSession? session = null)
    {
        lock (_gate)
        {
            // An eviction can retire a row and a later binding request recreate one under the same
            // ufrag, so a session tearing itself down must not remove its successor. Passing the
            // session scopes the removal to the row that actually owns it.
            if (!_byUfrag.TryGetValue(key, out Row? row) ||
                (session is not null && !ReferenceEquals(row.Session, session)))
            {
                return false;
            }

            RemoveLocked(row);
            return true;
        }
    }

    /// <summary>
    /// Drops rows that have gone quiet: STUN-only ones after <see cref="StunOnlyIdleMs"/>, established ones
    /// after <see cref="EstablishedIdleMs"/>. Returns the sessions it detached so the caller can dispose
    /// them — the table holds no disposables of its own, but an association it forgot without the caller
    /// closing it would leak the association's two threads.
    /// </summary>
    public List<TSession> Reap()
    {
        long now = _nowMs();
        List<TSession> reaped = [];

        lock (_gate)
        {
            // Materialized because RemoveLocked mutates _byUfrag.
            foreach (Row row in _byUfrag.Values.ToArray())
            {
                long idleBudget = row.IsEstablished ? EstablishedIdleMs : StunOnlyIdleMs;
                if (now - row.LastSeenMs < idleBudget)
                {
                    continue;
                }

                if (row.Session is not null)
                {
                    reaped.Add(row.Session);
                }

                RemoveLocked(row);
            }
        }

        return reaped;
    }

    private void RemoveLocked(Row row)
    {
        _byUfrag.Remove(row.Key);

        foreach (IPEndPoint address in row.Addresses)
        {
            // Only if the entry is still ours. An address is never stolen, so it can only ever be ours, but
            // the check keeps that true if the attach rule is loosened later.
            if (_byAddress.TryGetValue(address, out Row? owner) && ReferenceEquals(owner, row))
            {
                _byAddress.Remove(address);
            }
        }

        if (row.IsEstablished)
        {
            _establishedCount--;
        }
        else
        {
            _pendingCount--;
        }
    }

    /// <summary>
    /// Adds a source address to a row's secondary key set, if the address is free and the row is under
    /// <see cref="MaxAddressesPerUfrag"/>. An address already mapped — to this row or to any other — is left
    /// exactly where it is. Stealing one would hand an off-path attacker a one-packet hijack of somebody
    /// else's DTLS egress for the cost of a replayed USERNAME.
    /// </summary>
    private void AttachAddress(Row row, IPEndPoint source)
    {
        if (_byAddress.ContainsKey(source) || row.Addresses.Count >= MaxAddressesPerUfrag)
        {
            return;
        }

        row.Addresses.Add(source);
        _byAddress[source] = row;
    }

    private static WebRtcDirectSessionKey KeyFor(string serverUfrag, IPEndPoint normalizedSource) =>
        new(serverUfrag, normalizedSource.AddressFamily == AddressFamily.InterNetworkV6);

    private static WebRtcDirectRoute<TSession> Answer(
        WebRtcDirectRouteAction action, WebRtcDirectSessionKey key, STUNMessage request, IPEndPoint source) =>
        new(action, key, WebRtcDirectStun.BuildBindingSuccessResponse(request, source, key.ServerUfrag), null, null);

    private sealed class Row(WebRtcDirectSessionKey key, long now, long admissionSeq, IPEndPoint egress)
    {
        public WebRtcDirectSessionKey Key { get; } = key;

        /// <summary>Total admission order, for oldest-first eviction. Never changes once set.</summary>
        public long AdmissionSeq { get; } = admissionSeq;

        /// <summary>
        /// Where this peer's DTLS replies go, fixed at admission and deliberately without a setter.
        /// Pinning it to the first DTLS record instead loses a race an attacker can enter: observing one
        /// binding request reveals server_ufrag, which IS the MESSAGE-INTEGRITY key, so an off-path party
        /// can mint a valid request, get its address attached, and beat the honest peer to sending a
        /// record. The admitting request cannot be raced the same way -- by the time an attacker has seen
        /// the ufrag the row already exists, so it can never be the admitter.
        /// </summary>
        public IPEndPoint Egress { get; private set; } = egress;

        /// <summary>Whether a USE-CANDIDATE check has already chosen <see cref="Egress"/>.</summary>
        public bool EgressNominated { get; private set; }

        /// <summary>
        /// Whether a DTLS record has actually been routed to this row, which is what freezes
        /// <see cref="Egress"/> for good.
        /// <para>
        /// Deliberately NOT <c>Session is not null</c>. A session object is allocated the instant a
        /// row is admitted -- <c>Dispatch</c> admits on the first ordinary check and <c>Admit</c>
        /// attaches before the receive loop looks at the next datagram -- so gating on the session
        /// disabled nomination entirely in the only ordering production can produce. Allocating the
        /// object and selecting the candidate pair are different questions.
        /// </para>
        /// </summary>
        public bool DtlsRouted { get; private set; }

        /// <summary>Records that this row has begun carrying DTLS, after which nothing moves the egress.</summary>
        public void MarkDtlsRouted() => DtlsRouted = true;

        /// <summary>
        /// Moves the reply address to the pair the dialer nominated, which RFC 8445 sections 7.3.2
        /// and 12.1.1 require an ICE-lite endpoint to use. A dialer that probes several candidates
        /// may well send its first ordinary check from one whose return path does not work and then
        /// nominate a different one, so freezing on the first check seen leaves the nominated pair
        /// unable to finish its handshake.
        /// <para>
        /// Narrow on purpose, so the protection against arbitrary STUN moving a live connection
        /// stays intact: only a nominating check moves it, only the first such check does, and only
        /// before the row has carried any DTLS. From the first record onwards it is frozen.
        /// </para>
        /// </summary>
        public void TryNominateEgress(IPEndPoint source)
        {
            if (EgressNominated || DtlsRouted || IsEstablished)
            {
                return;
            }

            EgressNominated = true;
            Egress = source;
        }

        public long LastSeenMs { get; set; } = now;

        /// <summary>
        /// The per-peer session, attached as soon as a DTLS record needs somewhere to go. Its presence
        /// answers "can this row receive DTLS", and NOTHING else -- see <see cref="IsEstablished"/>.
        /// </summary>
        public TSession? Session { get; set; }

        /// <summary>
        /// Set only once the DTLS handshake has actually completed. This, not <see cref="Session"/>, is
        /// what buys a row the long idle budget and exemption from eviction.
        /// <para>
        /// The two were one field until an automated security review found the consequence: a session is
        /// created the moment a binding request is admitted, because <c>Route</c> can only return
        /// <c>DeliverToSession</c> for a row that already has one. Treating that as established handed a
        /// peer an entry in the small, never-evictable, five-minute pool for the price of a STUN packet
        /// that costs nothing to forge, and <see cref="MaxEstablishedSessions"/> of them denied the
        /// listener outright. Attached-but-unhandshaken rows now stay in the cheap pool where they belong.
        /// </para>
        /// </summary>
        public bool IsEstablished { get; set; }

        public List<IPEndPoint> Addresses { get; } = [];
    }
}
