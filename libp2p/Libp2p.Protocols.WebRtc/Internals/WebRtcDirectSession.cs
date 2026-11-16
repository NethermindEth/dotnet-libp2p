// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Tls;
using SIPSorcery.Net;
using SIPSorcery.Net.SharpSRTP.DTLSSRTP;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// One peer's half of a webrtc-direct listener: the DTLS handshake and the SCTP association that
/// rides on it, fed datagrams by the listener's single receive loop and writing back through an
/// injected send callback rather than owning a socket.
/// <para>
/// A listener serves every peer from one UDP port, so nothing here may own a socket and nothing may
/// block the receive loop. SIPSorcery makes that possible because <see cref="DtlsSrtpTransport"/> is
/// a BouncyCastle <see cref="DatagramTransport"/> with no socket behind it: datagrams go in through
/// <see cref="DtlsSrtpTransport.WriteToRecvStream"/> and come out through
/// <see cref="DtlsSrtpTransport.OnDataReady"/>. This is the only file in the transport that couples
/// to SIPSorcery's DTLS/SCTP types; everything above it sees <see cref="IWebRtcDataChannel"/>.
/// </para>
/// </summary>
internal sealed class WebRtcDirectSession : IAsyncDisposable
{
    /// <summary>
    /// Listener-wide ceiling on DTLS handshakes in flight. Each one parks a dedicated OS thread for
    /// up to <see cref="DtlsHandshakeTimeoutMs"/> (see <see cref="StartHandshake"/>), so the cost is
    /// 32 threads and 32 half-open BouncyCastle handshake states, not 32 sockets. The semaphore
    /// itself is injected because the budget belongs to the listener, not to any one peer.
    /// </summary>
    public const int MaxConcurrentDtlsHandshakes = 32;

    /// <summary>
    /// The entire half-open budget for one peer. <see cref="DtlsSrtpTransport.DoHandshake"/> is
    /// blocking and uncancellable, and <see cref="DtlsSrtpTransport.Close"/> is a no-op while it
    /// runs because <see cref="DtlsSrtpTransport.Transport"/> is only assigned once it returns, so
    /// this timeout is the only thing that ever ends a stalled handshake.
    /// </summary>
    public const int DtlsHandshakeTimeoutMs = 10_000;

    /// <summary>
    /// Inbound datagrams accepted into the DTLS receive queue before the handshake completes.
    /// <see cref="DtlsSrtpTransport"/> queues them in an unbounded <c>ConcurrentQueue</c> and drains
    /// it with a 25ms busy-wait, so without a bound one source can grow that queue as fast as it can
    /// send. A full DTLS 1.2 flight exchange is a handful of records even with retransmits and
    /// certificate fragmentation; 128 leaves an order of magnitude of headroom. Dropping is safe
    /// because DTLS retransmits its own flights -- buffering unauthenticated bytes off the internet
    /// is not.
    /// </summary>
    private const int MaxPreHandshakeDatagrams = 128;

    /// <summary>
    /// The SCTP port both sides of a WebRTC data channel use, as <c>a=sctp-port:5000</c> in
    /// <see cref="WebRtcDirectAnswerSdp"/> says. SCTP-over-DTLS is single-homed to the one DTLS
    /// association, so the ports are a formality rather than a demultiplexing key.
    /// </summary>
    private const ushort SctpPort = 5000;

    private readonly Certificate _certificateChain;
    private readonly AsymmetricKeyParameter _privateKey;
    private readonly int _localUdpPort;
    private readonly SemaphoreSlim _handshakeSlots;
    private readonly Action<byte[], IPEndPoint> _send;
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();

    private IPEndPoint? _remoteEndPoint;


    private DtlsSrtpTransport? _dtls;
    private RTCSctpTransport? _sctp;
    private int _preHandshakeDatagrams;
    private bool _disposed;

    /// <param name="certificateChain">The listener's DTLS certificate, whose fingerprint is the
    /// <c>/certhash</c> it publishes.</param>
    /// <param name="privateKey">The key for <paramref name="certificateChain"/>.</param>
    /// <param name="localUdpPort">The listener's own UDP port, reported to SCTP as the DTLS port.</param>
    /// <param name="handshakeSlots">The listener-wide handshake budget; see
    /// <see cref="MaxConcurrentDtlsHandshakes"/>.</param>
    /// <param name="send">Writes one datagram to one destination on the listener's shared socket.</param>
    public WebRtcDirectSession(
        Certificate certificateChain,
        AsymmetricKeyParameter privateKey,
        int localUdpPort,
        SemaphoreSlim handshakeSlots,
        Action<byte[], IPEndPoint> send)
    {
        _certificateChain = certificateChain;
        _privateKey = privateKey;
        _localUdpPort = localUdpPort;
        _handshakeSlots = handshakeSlots;
        _send = send;
    }

    /// <summary>
    /// Completes once DTLS has finished AND the SCTP association has been started, which is the
    /// point at which <see cref="RemoteFingerprint"/> and <see cref="Muxer"/> are readable. Faults
    /// on a failed or timed-out handshake and on disposal; it never cancels, so a caller still needs
    /// its own token when awaiting it.
    /// </summary>
    public Task Established => _established.Task;

    /// <summary>
    /// The fingerprint of the certificate the peer actually presented, for the caller to check
    /// against the <c>/certhash</c> in the multiaddr it dialled. Null until <see cref="Established"/>
    /// completes; the completion is what publishes it.
    /// </summary>
    public DtlsFingerprint? RemoteFingerprint { get; private set; }

    /// <summary>
    /// The association as a libp2p stream muxer. Published a moment before
    /// <see cref="Established"/> completes -- it has to exist before the association starts
    /// delivering, see <see cref="RunHandshake"/> -- so a caller reads it only after awaiting
    /// <see cref="Established"/>, never by polling it.
    /// </summary>
    public WebRtcDirectSctpMuxer? Muxer { get; private set; }

    /// <summary>
    /// Where this session's datagrams go, pinned by the first DTLS record and never moved. Null
    /// while the session is still STUN-only.
    /// </summary>
    public IPEndPoint? RemoteEndPoint
    {
        get
        {
            lock (_lock)
            {
                return _remoteEndPoint;
            }
        }
    }

    /// <summary>
    /// Hands one DTLS record from the listener's receive loop to this peer's DTLS stack. Never
    /// blocks and never throws: the loop that calls it serves every peer on the port.
    /// </summary>
    /// <param name="source">Where the datagram actually came from, for admission.</param>
    /// <param name="egress">Where this session's replies go, fixed when the row was admitted.</param>
    public void ReceiveDtls(byte[] datagram, IPEndPoint source, IPEndPoint egress)
    {
        DtlsSrtpTransport dtls;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            IPEndPoint normalizedSource = WebRtcDirectStun.Normalize(source);
            IPEndPoint normalized = WebRtcDirectStun.Normalize(egress);

            // Admission is decided by the SELECTED CANDIDATE PAIR, which is what `egress` carries:
            // fixed when the row was admitted and then only ever moved by a USE-CANDIDATE check,
            // before any DTLS flowed. Deciding it by whichever address sent the first record instead
            // hands the choice to whoever is quickest, and an endpoint that attached itself with the
            // public STUN integrity key is quick enough -- one inert record and the honest peer is
            // refused for the rest of the handshake. RFC 8445 sections 7.3.2 and 12.1.1 make the
            // nominated pair the one that carries media, so it is the one that carries DTLS too.
            if (!normalized.Equals(normalizedSource))
            {
                return;
            }

            if (_remoteEndPoint is null)
            {
                // ponytail: the egress is pinned to the source of the first DTLS record and never
                // moves, so a peer that genuinely migrates source port mid-connection -- a NAT
                // rebind, a CGNAT port reassignment -- is dropped and has to redial, where
                // go-libp2p would follow it to the new address. Deliberate: the only evidence a
                // new address could offer is an integrity-valid STUN binding request, and the
                // MESSAGE-INTEGRITY key is the server ufrag carried in that same packet, so it
                // proves nothing an off-path observer could not forge. Following it would turn one
                // observed packet into a connection hijack. The upgrade path is real ICE with
                // consent freshness (RFC 7675) -- an address change accepted only after a binding
                // exchange keyed on a secret the observer does not hold -- which needs an ICE agent
                // this transport does not have.
                _remoteEndPoint = normalized;
            }
            else if (!_remoteEndPoint.Equals(normalized))
            {
                return;
            }

            if (_dtls is null)
            {
                // ponytail: there is no DTLS HelloVerifyRequest cookie gate in front of this, so a
                // blind spoofer that sends one integrity-valid binding request and one DTLS record
                // holds a slot for the full DtlsHandshakeTimeoutMs. 32 slots / 10s is ~3.2 forced
                // handshakes per second to hold the listener at zero admission capacity, from a
                // single spoofed source, with no round trip completed. It cannot be fixed here:
                // DtlsSrtpTransport exposes only DoHandshake(out string), and it forwards to
                // IDtlsPeer.DoHandshake(out string, DatagramTransport, DtlsRequest = null) leaving
                // the request at its default, so there is no way to reach the cookie exchange from
                // this side of it. The upgrade path is to run BouncyCastle's DtlsVerifier in the
                // listener's receive loop -- answer a ClientHello with a HelloVerifyRequest and
                // create a session only once the cookie comes back -- and then hand the verified
                // DtlsRequest to the three-argument DoHandshake that SharpSRTP.DTLS.DtlsServer
                // already exposes, bypassing DtlsSrtpTransport's one-argument wrapper.
                if (!_handshakeSlots.Wait(0))
                {
                    // Dropped, not refused: no state is allocated and no reply is sent, so the
                    // session stays STUN-only and the peer's DTLS retransmit gets another chance
                    // once a slot frees.
                    return;
                }

                try
                {
                    StartHandshake(_remoteEndPoint);
                }
                catch (Exception ex)
                {
                    // A slot that is taken and never given back is permanent capacity loss, so it
                    // is released on every path that does not reach RunHandshake's finally.
                    _handshakeSlots.Release();
                    Fail(ex);
                    return;
                }
            }

            dtls = _dtls!;

            if (!dtls.IsHandshakeComplete() && ++_preHandshakeDatagrams > MaxPreHandshakeDatagrams)
            {
                return;
            }
        }

        // Outside the lock: this hands the bytes to a queue the handshake thread is draining, and
        // the receive loop must not be able to meet that thread here.
        dtls.WriteToRecvStream(datagram);
    }

    public async ValueTask DisposeAsync()
    {
        DtlsSrtpTransport? dtls;
        RTCSctpTransport? sctp;
        WebRtcDirectSctpMuxer? muxer;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dtls = _dtls;
            sctp = _sctp;
            muxer = Muxer;
        }

        Fail(new ObjectDisposedException(nameof(WebRtcDirectSession)));

        if (muxer is not null)
        {
            await muxer.DisposeAsync();
        }

        // The muxer closes the SCTP transport it was given; this covers the window between Start
        // and the muxer existing.
        Swallow(() => sctp?.Close());

        // A no-op if DoHandshake is still running -- Transport is only assigned once it returns --
        // so a handshake in flight still runs to its own timeout and releases its slot there.
        Swallow(() => dtls?.Close());
    }

    /// <summary>
    /// Creates the DTLS stack for this peer and starts the handshake on a thread of its own.
    /// Called under <see cref="_lock"/> with a handshake slot already held.
    /// <para>
    /// <see cref="TaskCreationOptions.LongRunning"/>, not <c>Task.Run</c>: DoHandshake blocks for up
    /// to ten seconds, so on the thread pool 32 concurrent handshakes would eat 32 pool threads and
    /// stall every unrelated continuation in the process until the pool's injection rate caught up.
    /// </para>
    /// </summary>
    private void StartHandshake(IPEndPoint remoteEndPoint)
    {
        // The listener answers a=setup:passive, so it is the DTLS server. See WebRtcDirectRoles.
        DtlsSrtpServer server = new(_certificateChain, _privateKey);
        DtlsSrtpTransport dtls = new(server)
        {
            TimeoutMilliseconds = DtlsHandshakeTimeoutMs,
        };

        // Subscribed before the handshake starts: the server's first flight is produced inside
        // DoHandshake, and an unsubscribed OnDataReady would discard it silently.
        dtls.OnDataReady += data => _send(data, remoteEndPoint);
        _dtls = dtls;

        _ = Task.Factory.StartNew(
            () => RunHandshake(dtls),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private void RunHandshake(DtlsSrtpTransport dtls)
    {
        try
        {
            if (!dtls.DoHandshake(out string? handshakeError))
            {
                Fail(new InvalidOperationException($"DTLS handshake failed: {handshakeError}"));
                return;
            }

            Certificate? remoteCertificate = dtls.GetRemoteCertificate();
            if (remoteCertificate is null || remoteCertificate.IsEmpty)
            {
                Fail(new InvalidOperationException("DTLS peer presented no certificate, so there is nothing to match against /certhash."));
                return;
            }

            // Published before Established completes, so that completion is what makes it visible.
            RemoteFingerprint = DtlsFingerprint.FromRtcFingerprint(DtlsUtils.Fingerprint(remoteCertificate));

            RTCSctpTransport sctp = new(SctpPort, SctpPort, _localUdpPort);

            // Subscribed before Start, not after it. RTCSctpTransport's constructor -- not Start --
            // is what creates the RTCPeerSctpAssociation the muxer hangs its handlers on, while
            // Start is what spawns the thread that reads packets into it. Building the muxer
            // afterwards leaves a window in which the association is already delivering: a DCEP
            // OPEN that lands in it reaches no OnNewDataChannel handler and the peer's first
            // inbound stream is lost with no error anywhere.
            WebRtcDirectSctpMuxer muxer = new(sctp, isDtlsClient: WebRtcDirectRoles.ListenerIsDtlsClient);

            lock (_lock)
            {
                if (_disposed)
                {
                    // DisposeAsync cannot reach a muxer this thread has not published yet, and an
                    // abandoned one keeps a 20ms buffered-amount timer -- and its own root -- alive
                    // for the life of the process. Its DisposeAsync closes the transport too.
                    muxer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    return;
                }

                _sctp = sctp;
                Muxer = muxer;
            }

            // dtls.Transport, NOT dtls. Both satisfy DatagramTransport, so the wrong one compiles
            // and leaves DTLS looking healthy while SCTP reads raw records off the pre-decryption
            // queue and writes plaintext chunks straight to the wire; the association then simply
            // never connects. WebRtcDirectSessionTests covers exactly that.
            //
            // Start alone brings the association up on the passive side. Associate() must not be
            // called here: it emits an INIT, and SIPSorcery documents a race when both ends do.
            sctp.Start(dtls.Transport, isDtlsClient: WebRtcDirectRoles.ListenerIsDtlsClient);

            _established.TrySetResult();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            // The only release on the success path too: a slot is held for the handshake, not for
            // the connection.
            _handshakeSlots.Release();
        }
    }

    private void Fail(Exception exception)
    {
        if (_established.TrySetException(exception))
        {
            // Nothing need ever await Established -- a listener may simply drop a session that did
            // not come up -- so observe it rather than let it surface as an unobserved task
            // exception at the next GC.
            _ = _established.Task.Exception;
        }
    }

    private static void Swallow(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Teardown of a stack that never established, or already aborted, is not a failure of
            // disposal.
        }
    }
}
