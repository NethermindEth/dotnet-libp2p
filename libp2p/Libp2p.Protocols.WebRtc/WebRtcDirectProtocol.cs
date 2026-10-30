// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using SIPSorcery.Net;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.WebRtc.Tests")]

namespace Nethermind.Libp2p.Protocols.WebRtc;

public class WebRtcDirectProtocol : ITransportProtocol
{
    private const string NoiseChannelLabel = "noise";

    /// <summary>
    /// How often <see cref="WebRtcDirectListenerTable{TSession}.Reap"/> runs. The table enforces its
    /// TTLs only as fast as something calls it, and the shortest of them --
    /// <see cref="WebRtcDirectListenerTable{TSession}.StunOnlyIdleMs"/>, 15s -- is the one that frees
    /// flood residue, so the sweep has to be well inside it.
    /// </summary>
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<WebRtcDirectProtocol>? _logger;

    /// <summary>
    /// Throttles binding requests from sources no row owns yet, which is the only path that
    /// allocates. Owned here rather than by the table so one process-wide budget covers every listen
    /// address.
    /// </summary>
    private readonly WebRtcDirectRateLimiter _newSessionRateLimiter = new();

    /// <summary>
    /// The listener-wide DTLS handshake budget shared by every <see cref="WebRtcDirectSession"/>.
    /// Sized at <see cref="WebRtcDirectSession.MaxConcurrentDtlsHandshakes"/> because each slot parks
    /// a dedicated OS thread for up to <see cref="WebRtcDirectSession.DtlsHandshakeTimeoutMs"/>.
    /// </summary>
    private readonly SemaphoreSlim _handshakeSlots =
        new(WebRtcDirectSession.MaxConcurrentDtlsHandshakes, WebRtcDirectSession.MaxConcurrentDtlsHandshakes);

    private readonly RTCCertificate2 _certificate;

    /// <summary>
    /// The same certificate as <see cref="_certificate"/> in the form the listener needs.
    /// <see cref="DtlsSrtpServer"/> takes a BouncyCastle <see cref="Certificate"/> chain and its key,
    /// not an <see cref="RTCCertificate2"/>, so the chain is retained rather than discarded once
    /// SIPSorcery's wrapper has been built from it.
    /// </summary>
    private readonly Certificate _certificateChain;
    private readonly AsymmetricKeyParameter _privateKey;

    private readonly DtlsFingerprint _fingerprint;

    /// <summary>
    /// How the dial half learns the certificate the peer presented in DTLS, injectable so the
    /// /certhash check stays coverable in CI without a live peer that lies about its certificate.
    /// </summary>
    private readonly Func<RTCPeerConnection, RTCDtlsFingerprint?> _readRemoteFingerprint;

    /// <summary>
    /// The listen half's counterpart. There is no /certhash to compare against on this side -- the
    /// listener dialled nobody -- so this value is what binds the certificate to the peer's libp2p
    /// identity, through the Noise prologue. Injectable for the same reason as
    /// <see cref="_readRemoteFingerprint"/>: a listener whose view of it is wrong must be unable to
    /// finish the handshake, and that is only observable in CI if the value can be perturbed.
    /// </summary>
    private readonly Func<WebRtcDirectSession, DtlsFingerprint?> _readSessionFingerprint;

    public WebRtcDirectProtocol(ILoggerFactory? loggerFactory = null)
        : this(pc => pc.RemotePeerDtlsFingerprint, session => session.RemoteFingerprint, loggerFactory)
    {
    }

    internal WebRtcDirectProtocol(
        Func<RTCPeerConnection, RTCDtlsFingerprint?> readRemoteFingerprint,
        Func<WebRtcDirectSession, DtlsFingerprint?> readSessionFingerprint,
        ILoggerFactory? loggerFactory = null)
    {
        _readRemoteFingerprint = readRemoteFingerprint;
        _readSessionFingerprint = readSessionFingerprint;
        _logger = loggerFactory?.CreateLogger<WebRtcDirectProtocol>();
        (_certificate, _certificateChain, _privateKey, _fingerprint) = CreateLocalCertificate();
    }

    public string Id => "webrtc-direct";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];

    public static bool IsAddressMatch(Multiaddress addr) => WebRtcDirectMultiaddr.IsWebRtcDirect(addr);

    /// <summary>
    /// Serves every inbound peer from one UDP port, as webrtc-direct requires: there is no signalling
    /// channel and no per-peer socket, so a datagram's only identity is its first byte (RFC 7983) and,
    /// for a binding request, the <c>server_ufrag</c> in its USERNAME.
    /// <para>
    /// Everything this loop does with a datagram comes from
    /// <see cref="WebRtcDirectListenerTable{TSession}.Route"/>. In particular it does NOT consult an
    /// address map of its own first: an established peer keeps sending binding requests for the life
    /// of the connection, and routing one into DTLS instead of answering it looks fine for the two
    /// seconds of a loopback test and then drops every real dialer inside twenty. See the Route
    /// remarks for the SIPSorcery timers behind that number.
    /// </para>
    /// </summary>
    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        using UdpClient udp = Bind(listenAddr.ToEndPoint());
        IPEndPoint endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;

        context.ListenerReady(WebRtcDirectMultiaddr.Build(endpoint, _fingerprint));
        _logger?.LogInformation("WebRTC-Direct listening on {Endpoint}", endpoint);

        WebRtcDirectListenerTable<WebRtcDirectSession> table = new(rateLimiter: _newSessionRateLimiter);

        // Separate from the caller's token so the sessions this listener started are torn down when
        // it stops, whether it stopped on cancellation or on a socket failure.
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task reaping = ReapAsync(table, stopping.Token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try
                {
                    packet = await udp.ReceiveAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // A previous send drew an ICMP port-unreachable, which the OS reports on the next
                    // receive. It says nothing about this socket's health, so the loop goes on.
                    _logger?.LogTrace(ex, "WebRTC-Direct receive reported {Error}", ex.SocketErrorCode);
                    continue;
                }

                try
                {
                    // UdpClient.ReceiveAsync hands back an array sized to the datagram, so there is no
                    // pooled tail to slice off. That matters because the APIs below take byte[] and
                    // read .Length as the message length -- STUN parsing, MESSAGE-INTEGRITY and the
                    // DTLS record queue would all be fed trailing zeroes by an oversized buffer.
                    Dispatch(context, table, udp, endpoint, packet.RemoteEndPoint, packet.Buffer, stopping.Token);
                }
                catch (Exception ex)
                {
                    // One malformed datagram must never take the listener down: it would be a remote
                    // kill switch for every peer on the port. WebRtcDirectStun exists to make this
                    // unreachable (STUNMessage.ParseSTUNMessage dereferences a null header on a 1..19
                    // byte datagram), and this is the backstop for the next one of those.
                    _logger?.LogDebug(ex, "Dropped WebRTC-Direct datagram from {Remote}", packet.RemoteEndPoint);
                }
            }
        }
        finally
        {
            stopping.Cancel();
            await reaping;
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        (IPEndPoint endpoint, DtlsFingerprint expectedFingerprint) = WebRtcDirectMultiaddr.Parse(remoteAddr);

        RTCPeerConnection pc = CreatePeerConnection();

        try
        {
            // Subscribed before anything can arrive: ondatachannel carries no backlog, so a stream
            // the peer opens before this point would be lost rather than delayed.
            IAsyncEnumerable<IWebRtcDataChannel> inboundStreams = SubscribeToInboundStreams(pc);

            RTCDataChannel noiseDataChannel = await pc.createDataChannel(NoiseChannelLabel, new RTCDataChannelInit { negotiated = true, id = 0 });

            // Attached HERE, before the connection is reported up, and not after. The listener is
            // the Noise initiator and writes message 1 the moment its own SCTP association
            // connects -- measured at ~20ms before this side finishes opening -- while
            // RTCDataChannel.GotData invokes `onmessage?.Invoke(...)`, so a message that arrives
            // with no subscriber is DROPPED, not queued. Attaching after the connection loses
            // message 1 on about two runs in three and leaves both halves waiting on each other
            // until the dial times out. Same hazard, and the same fix, as subscribing the SCTP
            // muxer before its association can deliver.
            //
            // Ownership passes to RunNoiseHandshakeAsync below, which closes it as
            // webrtc-direct.md Connection Security (3) requires. On any throw before that, the
            // pc.close() in this method's finally closes the channel under it.
            WebRtcStream noiseStream = new(new RtcDataChannelAdapter(noiseDataChannel));

            Task noiseOpenTask = WaitForDataChannelOpenAsync(noiseDataChannel, token);
            Task connectionTask = WaitForConnectionAsync(pc, token);

            RTCSessionDescriptionInit offer = pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
            ValidateExpectedFingerprint(WebRtcDirectSdp.ExtractFingerprint(offer.sdp ?? string.Empty), _fingerprint, "offer");
            await pc.setLocalDescription(offer);

            // webrtc-direct has no signalling channel: the offer is never sent anywhere. The answer
            // is synthesised locally from the multiaddr, and the listener identifies this dial from
            // the STUN USERNAME alone, which carries the server ufrag built below.
            RTCSessionDescriptionInit answer = new()
            {
                type = RTCSdpType.answer,
                sdp = WebRtcDirectAnswerSdp.Build(endpoint, expectedFingerprint, pc.GetRtpChannel().LocalIcePassword),
            };
            EnsureRemoteDescriptionApplied(pc.setRemoteDescription(answer), "answer");

            await connectionTask;
            await noiseOpenTask;

            // The answer above only claims the /certhash; this checks the certificate the peer
            // actually presented in the DTLS handshake against it. Read through the accessor
            // injected by #422 so the enforcement stays coverable in CI without a live peer.
            ValidateRemoteFingerprint(_readRemoteFingerprint(pc), expectedFingerprint);

            using INewConnectionContext connectionContext = context.CreateConnection();
            using CancellationTokenRegistration closed = connectionContext.Token.Register(pc.close);
            connectionContext.State.LocalAddress = pc.GetRtpChannel().RTPLocalEndPoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = endpoint.ToMultiaddress(ProtocolType.Udp);

            // The dialer is _A_, the Noise RESPONDER. Both role-dependent values here come from
            // WebRtcDirectRoles, which carries the spec sentence behind each one; getting either
            // backwards is invisible to a loopback test, since both halves would be wrong
            // together, and fails against every real peer.
            (DtlsFingerprint dialerFingerprint, DtlsFingerprint listenerFingerprint) =
                WebRtcDirectRoles.OrderFingerprints(isListener: false, local: _fingerprint, remote: expectedFingerprint);

            connectionContext.State.RemotePublicKey = await RunNoiseHandshakeAsync(
                noiseStream,
                context.Peer.Identity,
                WebRtcNoisePrologue.Build(dialerFingerprint, listenerFingerprint),
                isInitiator: !WebRtcDirectRoles.ListenerIsNoiseInitiator,
                token);

            // Ids are chosen here rather than left to SIPSorcery. RTCDataChannelCollection seeds
            // its counter at ushort.MaxValue - 1 and adds 2 unchecked, so the first id it offers
            // the DTLS client is 0 -- the reserved Noise id, which the handshake above has just
            // closed and released. It only hands out 2 while id 0 is still occupied, which is no
            // longer true by the time a stream is opened. RFC 8832 section 6 parity is ours to
            // honour either way, and FirstOutboundStreamId is where it is written down.
            int nextStreamId = WebRtcDirectRoles.FirstOutboundStreamId(isDtlsClient: !WebRtcDirectRoles.ListenerIsDtlsClient);

            async Task<IWebRtcDataChannel> OpenStreamAsync(CancellationToken streamToken)
            {
                int streamId = Interlocked.Add(ref nextStreamId, 2) - 2;
                if (streamId > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The SCTP stream id space for this side of the association is exhausted; stream ids are never reused.");
                }

                // A libp2p stream carries its own protocol negotiation, so the DCEP label and
                // subprotocol have nothing to say -- the same empty pair WebRtcDirectSctpMuxer
                // puts in the OPEN it builds by hand. createDataChannel waits for the peer's DCEP
                // ACK with no timeout of its own and takes no token, so the token is applied here.
                RTCDataChannel channel = await pc
                    .createDataChannel(string.Empty, new RTCDataChannelInit { id = (ushort)streamId })
                    .WaitAsync(streamToken);

                // A channel created while the peer connection is not connected is parked as a
                // placeholder and returned unopened, and RTCDataChannel.send logs a warning and
                // DISCARDS on a transport that is not connected. Refusing it here turns that
                // silent data loss into a failed stream open.
                if (!channel.IsOpened)
                {
                    channel.close();
                    throw new InvalidOperationException($"WebRTC data channel {streamId} did not open.");
                }

                return new RtcDataChannelAdapter(channel);
            }

            await RunSessionAsync(connectionContext, OpenStreamAsync, inboundStreams, token);
        }
        finally
        {
            pc.close();
            pc.Dispose();
        }
    }

    /// <summary>
    /// Binds the one socket this listener serves every peer from. <c>/ip6/::</c> gets a dual-mode
    /// socket so a v4 dialer is reachable on the same port;
    /// <see cref="WebRtcDirectStun.Normalize"/> folds the <c>::ffff:a.b.c.d</c> form such a socket
    /// delivers, which is why every source below goes through the table rather than being keyed raw.
    /// </summary>
    private static UdpClient Bind(IPEndPoint endpoint)
    {
        if (!endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            return new(endpoint);
        }

        UdpClient dualMode = new(AddressFamily.InterNetworkV6);
        dualMode.Client.DualMode = true;
        dualMode.Client.Bind(endpoint);
        return dualMode;
    }

    private void Dispatch(
        ITransportContext context,
        WebRtcDirectListenerTable<WebRtcDirectSession> table,
        UdpClient udp,
        IPEndPoint localEndpoint,
        IPEndPoint source,
        byte[] datagram,
        CancellationToken token)
    {
        WebRtcDirectRoute<WebRtcDirectSession> route = table.Route(source, datagram);

        switch (route.Action)
        {
            case WebRtcDirectRouteAction.AnswerStun:
                Send(udp, route.Response!, source);
                break;

            case WebRtcDirectRouteAction.AnswerStunAndAdmit:
                Send(udp, route.Response!, source);
                // Admitting may have evicted the oldest pending row to make space, and since a pending
                // row can own a half-finished session, that session has to be disposed here. The table
                // holds no disposables of its own and dropping one would leak its DTLS stack and the
                // thread parked in DoHandshake.
                if (route.EvictedSession is { } evicted)
                {
                    _ = evicted.DisposeAsync().AsTask();
                }
                Admit(context, table, udp, localEndpoint, route.Key, token);
                break;

            case WebRtcDirectRouteAction.DeliverToSession:
                // route.Egress, NOT source. The session pins its egress to whatever address it first
                // sees a DTLS record from, and that race is one an off-path attacker can enter: the
                // MESSAGE-INTEGRITY key is the server ufrag carried in the same packet, so observing
                // one binding request is enough to mint a valid one, get an address attached to the
                // row and then beat the honest peer to sending a record. Handing the session the
                // row's egress -- fixed at admission, which cannot be raced, see
                // WebRtcDirectListenerTable.Row.Egress -- means records from any attached address are
                // delivered, DTLS authenticates them itself, and every reply still goes to the peer
                // that admitted the row. WebRtcDirectListenerTableTests
                // .Route_PinsEgressToTheAdmittingAddress_NotToWhoeverSendsDtlsFirst pins the value;
                // this is the line that honours it.
                route.Session!.ReceiveDtls(datagram, route.Egress!);
                break;

            case WebRtcDirectRouteAction.Drop:
            default:
                // Nothing is sent back, ever. A listener that answered an unvalidated source with so
                // much as an error would be a reflection primitive.
                break;
        }
    }

    /// <summary>
    /// Creates the session for a freshly admitted row and attaches it to the table at once.
    /// <para>
    /// Attaching it here rather than when the handshake completes is forced:
    /// <see cref="WebRtcDirectListenerTable{TSession}.Route"/> drops a DTLS record whose row carries
    /// no session, so a session published only after its handshake finished would never be fed the
    /// records that finish it.
    /// </para>
    /// </summary>
    private void Admit(
        ITransportContext context,
        WebRtcDirectListenerTable<WebRtcDirectSession> table,
        UdpClient udp,
        IPEndPoint localEndpoint,
        WebRtcDirectSessionKey key,
        CancellationToken token)
    {
        WebRtcDirectSession session = new(
            _certificateChain,
            _privateKey,
            localEndpoint.Port,
            _handshakeSlots,
            (data, destination) => Send(udp, data, destination));

        // Attach only. The row stays in the cheap pending pool on the short idle budget until DTLS
        // actually completes; promotion happens in DriveSessionAsync. Attaching is what lets Route
        // return DeliverToSession, and it must not by itself buy a place in the established pool.
        if (!table.AttachSession(key, session))
        {
            // The row went away between admission and here. The session owns no threads yet, but it
            // must not be left for the GC either.
            _ = session.DisposeAsync().AsTask();
            return;
        }

        // ponytail: fire and forget, with the session's own teardown in its finally. Awaiting these
        // on the way out would block listener shutdown on Upgrade, which runs for the life of each
        // connection; QuicProtocol.ListenAsync does the same with its accepted connections. Cancelling
        // `stopping` is what reaches them. Upgrade to a tracked set if shutdown ever has to be
        // observably complete.
        _ = DriveSessionAsync(context, table, key, session, localEndpoint, token);
    }

    /// <summary>
    /// Waits for one admitted session to come up and turns it into an upgraded libp2p connection.
    /// </summary>
    private async Task DriveSessionAsync(
        ITransportContext context,
        WebRtcDirectListenerTable<WebRtcDirectSession> table,
        WebRtcDirectSessionKey key,
        WebRtcDirectSession session,
        IPEndPoint localEndpoint,
        CancellationToken token)
    {
        try
        {
            // Established faults on failure, timeout and disposal but never cancels, so the token is
            // the only thing that unparks this when the listener stops.
            await session.Established.WaitAsync(token);

            // DTLS is up, so this row has earned the long idle budget and exemption from eviction.
            // A full established pool is refused here rather than at attach, which keeps capacity for
            // peers that finish a handshake instead of handing it to anyone who can forge a binding
            // request.
            if (!table.MarkEstablished(key, session))
            {
                throw new InvalidOperationException("WebRTC-Direct session could not be established: the row is gone or the established cap is full.");
            }

            // All three are published by the completion above, not polled for. The fingerprint is
            // read through the injected accessor so the binding below stays coverable in CI.
            if (_readSessionFingerprint(session) is not { } remoteFingerprint ||
                session.Muxer is not { } muxer ||
                session.RemoteEndPoint is not { } remoteEndPoint)
            {
                throw new InvalidOperationException("Established WebRTC-Direct session published no fingerprint, muxer or peer address.");
            }

            // Registered before the association is connected, so nothing the peer puts on stream 0
            // can arrive before there is a channel to route it to: the muxer drops a frame whose
            // stream it does not yet know. The dial half has the same hazard in a sharper form --
            // see the comment in DialAsync.
            WebRtcStream noiseStream = new(muxer.GetNegotiatedChannel(WebRtcDirectSctpMuxer.NoiseStreamId));

            // Established only means sctp.Start() has returned; the association has not yet seen the
            // peer's INIT at that point. SctpAssociation.SendData on an association still in Closed
            // logs a warning and DISCARDS the message, so writing the Noise handshake here rather
            // than after Connected loses message one outright and both sides wait for ever.
            await muxer.Connected.WaitAsync(token);

            using INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = localEndpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = remoteEndPoint.ToMultiaddress(ProtocolType.Udp);

            // The listener is _B_, the Noise INITIATOR, and _B_'s fingerprint goes second in the
            // prologue. See WebRtcDirectRoles for the spec sentences behind both. There is no
            // /certhash to check the peer's certificate against -- the listener dialled nobody --
            // so the fingerprint is bound to the libp2p identity at Noise instead, through the
            // prologue.
            (DtlsFingerprint dialerFingerprint, DtlsFingerprint listenerFingerprint) =
                WebRtcDirectRoles.OrderFingerprints(isListener: true, local: _fingerprint, remote: remoteFingerprint);

            connectionContext.State.RemotePublicKey = await RunNoiseHandshakeAsync(
                noiseStream,
                context.Peer.Identity,
                WebRtcNoisePrologue.Build(dialerFingerprint, listenerFingerprint),
                isInitiator: WebRtcDirectRoles.ListenerIsNoiseInitiator,
                token);

            await RunSessionAsync(
                connectionContext,
                async streamToken => await muxer.OpenStreamAsync(streamToken),
                muxer.InboundChannels,
                token);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WebRTC-Direct inbound session {Ufrag} ended", key.ServerUfrag);
        }
        finally
        {
            table.Remove(key, session);
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs the Noise handshake on the negotiated stream-0 channel, returns the peer's identity key
    /// and discards the channel, which is all webrtc-direct.md Connection Security (3) leaves of it:
    /// "the datachannel used for the Noise handshake is closed". Nothing wraps the connection
    /// afterwards -- DTLS, not Noise, is what encrypts the streams that follow.
    /// <para>
    /// Takes an already-attached <paramref name="handshakeStream"/>, and owns it from here: the
    /// stream has to be subscribed to its data channel before the peer can send, which on the dial
    /// half is well before this is called. The handshake goes through
    /// <see cref="WebRtcStream"/> because the spec runs it "using the message framing described in
    /// the Multiplexing section", not as raw datachannel messages.
    /// </para>
    /// </summary>
    private static async Task<PublicKey> RunNoiseHandshakeAsync(
        WebRtcStream handshakeStream,
        Identity identity,
        byte[] prologue,
        bool isInitiator,
        CancellationToken token)
    {
        await using WebRtcStream stream = handshakeStream;
        return await WebRtcDirectNoiseHandshake.HandshakeAsync(
            new WebRtcStreamChannel(stream), identity, prologue, isInitiator, token);
    }

    /// <summary>
    /// The multi-stream half of a live connection, shared by both halves because they differ only
    /// in where a stream comes from: the listener opens and accepts through
    /// <see cref="WebRtcDirectSctpMuxer"/>, the dialer through the data channels
    /// <see cref="RTCPeerConnection"/> allocates for it. The shape is
    /// <see cref="Quic.QuicProtocol"/>'s -- a transport that is its own muxer answers
    /// <see cref="INewSessionContext.DialRequests"/> and upgrades each inbound stream.
    /// </summary>
    internal async Task RunSessionAsync(
        INewConnectionContext connectionContext,
        Func<CancellationToken, Task<IWebRtcDataChannel>> openStream,
        IAsyncEnumerable<IWebRtcDataChannel> inboundStreams,
        CancellationToken token)
    {
        using INewSessionContext session = connectionContext.UpgradeToSession();
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);

        // Live inbound streams, capped at the same number the listener's muxer allows. The cap
        // belongs here rather than in the muxer because only the listen half has a muxer: on the
        // dial half SIPSorcery allocates an inbound RTCDataChannel before this transport sees it,
        // so a peer we dialled could otherwise hold an unbounded number of live streams, each one
        // a data channel, a polling timer, a frame buffer and an Upgrade into the protocol stack.
        // Core's InboundStreamLimit does not cover this: it is opt-in per protocol and applies
        // after the stream has been built and negotiated.
        int liveInboundStreams = 0;

        // Task.Run because DialRequests is a blocking enumerable: it parks the calling thread
        // between requests rather than yielding it.
        Task dialling = Task.Run(async () =>
        {
            foreach (UpgradeOptions request in session.DialRequests)
            {
                IWebRtcDataChannel channel;
                try
                {
                    channel = await openStream(stopping.Token);
                }
                catch (Exception ex)
                {
                    // Nothing else ever completes this source, so a caller parked in
                    // DialAsync<TProtocol> would wait for the life of the process.
                    request.CompletionSource?.TrySetException(ex);
                    throw;
                }

                Carry(channel, request with { ModeOverride = UpgradeModeOverride.Dial });
            }
        }, stopping.Token);

        Task accepting = Task.Run(async () =>
        {
            await foreach (IWebRtcDataChannel channel in inboundStreams.WithCancellation(stopping.Token))
            {
                if (Interlocked.Increment(ref liveInboundStreams) > WebRtcDirectSctpMuxer.MaxConcurrentChannels)
                {
                    Interlocked.Decrement(ref liveInboundStreams);

                    // Closed rather than ignored: a stream left open and unread looks accepted to
                    // the peer, which is worse for it than a refusal it can see.
                    channel.Close();
                    continue;
                }

                Observe(CarryInboundStreamAsync(channel), $"stream {channel.StreamId}");
            }
        }, stopping.Token);

        try
        {
            // Either loop ending ends the connection: DialRequests completes when the session is
            // disconnected, and the inbound sequence completes when the association goes away. The
            // streams already carried keep running until the connection itself is torn down.
            Task first = await Task.WhenAny(dialling, accepting);
            Observe(first == dialling ? accepting : dialling, "session loop");
            await first;
        }
        finally
        {
            stopping.Cancel();
        }

        void Carry(IWebRtcDataChannel channel, UpgradeOptions options) =>
            Observe(CarryStreamAsync(connectionContext, channel, options), $"stream {channel.StreamId}");

        async Task CarryInboundStreamAsync(IWebRtcDataChannel channel)
        {
            try
            {
                await CarryStreamAsync(connectionContext, channel, new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
            }
            finally
            {
                // Released on the way out, which is what makes this a concurrency cap rather than
                // a lifetime one.
                Interlocked.Decrement(ref liveInboundStreams);
            }
        }
    }

    /// <summary>One libp2p stream, from its data channel up to the protocol stack that consumes it.</summary>
    private static async Task CarryStreamAsync(
        INewConnectionContext connectionContext,
        IWebRtcDataChannel channel,
        UpgradeOptions options)
    {
        await using WebRtcStream stream = new(channel);
        await connectionContext.Upgrade(new WebRtcStreamChannel(stream), options);
    }

    /// <summary>
    /// Surfaces the inbound data channels <see cref="RTCPeerConnection"/> hands the dial half as the
    /// same sequence <see cref="WebRtcDirectSctpMuxer.InboundChannels"/> gives the listen half.
    /// <para>
    /// Completed when the peer connection goes down, because that is the only thing that tells the
    /// session loop a dialled connection has ended: nothing else is watching liveness once the
    /// handshake is behind us.
    /// </para>
    /// </summary>
    private static IAsyncEnumerable<IWebRtcDataChannel> SubscribeToInboundStreams(RTCPeerConnection pc)
    {
        // ponytail: bounds the accept backlog, not the number of live streams -- SIPSorcery
        // allocates an inbound channel before this sees it, so the dial half has no equivalent of
        // the muxer's MaxConcurrentChannels. Per-connection, and only for a peer we chose to dial.
        // Fully qualified: Nethermind.Libp2p.Core has a Channel of its own.
        System.Threading.Channels.Channel<IWebRtcDataChannel> inbound =
            System.Threading.Channels.Channel.CreateBounded<IWebRtcDataChannel>(
            new BoundedChannelOptions(WebRtcDirectSctpMuxer.MaxConcurrentChannels)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        pc.ondatachannel += channel =>
        {
            RtcDataChannelAdapter adapter = new(channel);
            if (!inbound.Writer.TryWrite(adapter))
            {
                // Refused rather than queued: a stream that is accepted and then never read is
                // worse for the peer than one that is closed straight away.
                adapter.Close();
            }
        };

        pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                inbound.Writer.TryComplete();
            }
        };

        return inbound.Reader.ReadAllAsync();
    }

    /// <summary>
    /// Keeps a fire-and-forget task's failure out of <see cref="TaskScheduler.UnobservedTaskException"/>
    /// and in the log, where a stream that died on its own is worth seeing.
    /// </summary>
    private void Observe(Task task, string what) =>
        _ = task.ContinueWith(
            finished => _logger?.LogDebug(finished.Exception, "WebRTC-Direct {What} ended", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>
    /// Drives the table's TTLs, which it applies only when something asks it to, and disposes what it
    /// hands back: the table holds no disposables, so an association it forgot without the caller
    /// closing it would leak that association's two threads for the life of the process.
    /// </summary>
    private async Task ReapAsync(WebRtcDirectListenerTable<WebRtcDirectSession> table, CancellationToken token)
    {
        using PeriodicTimer timer = new(ReapInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (WebRtcDirectSession session in table.Reap())
                {
                    _logger?.LogDebug("Reaped an idle WebRTC-Direct session");
                    await session.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Send(UdpClient udp, byte[] datagram, IPEndPoint destination)
    {
        try
        {
            udp.Send(datagram, datagram.Length, destination);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The socket is gone or the destination is unreachable. Both are the peer's problem and
            // neither may propagate: this runs on the receive loop and on DTLS callbacks alike.
            _logger?.LogTrace(ex, "WebRTC-Direct send to {Destination} failed", destination);
        }
    }

    private RTCPeerConnection CreatePeerConnection()
    {
        RTCConfiguration config = new()
        {
            X_ICEIncludeAllInterfaceAddresses = false,
            X_GatherTimeoutMs = 2000,
            certificates2 = [_certificate],
        };

        return new RTCPeerConnection(config);
    }

    /// <summary>
    /// Internal rather than private so tests can obtain a certificate without naming BouncyCastle types.
    /// The test assembly sees both BouncyCastle.Crypto (via SIPSorcery) and BouncyCastle.Cryptography
    /// (via the Libp2p facade reference added in #422), which makes `Certificate`, `BcTlsCrypto` and
    /// `AsymmetricKeyParameter` ambiguous there; this assembly sees only the former, so it can hand the
    /// resolved types out.
    /// </summary>
    internal static (RTCCertificate2 Certificate, Certificate Chain, AsymmetricKeyParameter PrivateKey, DtlsFingerprint Fingerprint) CreateLocalCertificate()
    {
        (Certificate certificateChain, AsymmetricKeyParameter privateKey) =
            DtlsUtils.CreateSelfSignedTlsCert(new BcTlsCrypto());
        X509Certificate certificate = new(certificateChain.GetCertificateAt(0).GetEncoded());
        RTCCertificate2 rtcCertificate = new()
        {
            Certificate = certificate,
            PrivateKey = privateKey,
        };

        return (rtcCertificate, certificateChain, privateKey, DtlsFingerprint.FromRtcFingerprint(DtlsUtils.Fingerprint(certificateChain)));
    }

    private static Task WaitForConnectionAsync(RTCPeerConnection pc, CancellationToken token)
    {
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
            {
                connected.TrySetResult();
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                connected.TrySetException(new InvalidOperationException($"WebRTC connection state: {state}"));
            }
        };

        token.Register(() => connected.TrySetCanceled(token));
        return connected.Task;
    }

    private static Task WaitForDataChannelOpenAsync(RTCDataChannel channel, CancellationToken token)
    {
        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.onopen += () => opened.TrySetResult();
        channel.onclose += () => opened.TrySetException(new InvalidOperationException("Noise data channel closed before opening."));
        token.Register(() => opened.TrySetCanceled(token));
        return opened.Task;
    }

    internal static void ValidateRemoteFingerprint(RTCDtlsFingerprint? remote, DtlsFingerprint expected)
    {
        if (remote is null || !expected.Matches(remote))
        {
            throw new InvalidOperationException("Remote DTLS fingerprint does not match /certhash.");
        }
    }

    private static void ValidateExpectedFingerprint(DtlsFingerprint actual, DtlsFingerprint expected, string source)
    {
        bool algorithmMatches = actual.Algorithm.Equals(expected.Algorithm, StringComparison.OrdinalIgnoreCase);
        bool digestMatches = CryptographicOperations.FixedTimeEquals(actual.Value, expected.Value);
        if (!algorithmMatches || !digestMatches)
        {
            throw new InvalidOperationException($"DTLS fingerprint mismatch in {source} SDP.");
        }
    }

    private static void EnsureRemoteDescriptionApplied(SetDescriptionResultEnum result, string source)
    {
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to apply WebRTC remote {source} SDP: {result}.");
        }
    }
}
