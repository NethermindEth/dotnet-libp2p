// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using SIPSorcery.Net;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// The first end-to-end proof in this transport: a real SIPSorcery <see cref="RTCPeerConnection"/>
/// dialer, set up exactly as <c>WebRtcDirectProtocol.DialAsync</c> sets one up, against a bare
/// <see cref="UdpClient"/> with nothing behind it but <see cref="WebRtcDirectStun"/> and
/// <see cref="WebRtcDirectSession"/>. Everything below the session -- ICE, DTLS, SCTP, DCEP -- is
/// the real stack, which is the point: the parts of this transport that a loopback unit test cannot
/// reach are precisely the parts that have been wrong before.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WebRtcDirectSessionTests
{
    [Test]
    [CancelAfter(60_000)]
    public async Task Session_ReachesEstablished_WithTheDialersOwnCertificateFingerprint(CancellationToken token)
    {
        await using Listener listener = new();
        using Dialer dialer = new(listener.EndPoint, listener.Fingerprint);

        await listener.Session.Established.WaitAsync(token);

        Assert.Multiple(() =>
        {
            Assert.That(listener.Session.RemoteFingerprint, Is.Not.Null);
            Assert.That(listener.Session.RemoteFingerprint!.Algorithm, Is.EqualTo(dialer.Fingerprint.Algorithm));
            Assert.That(
                CryptographicOperations.FixedTimeEquals(listener.Session.RemoteFingerprint.Value, dialer.Fingerprint.Value),
                Is.True,
                "RemoteFingerprint must be the certificate the dialer actually presented, since that is what the caller checks against /certhash.");
            Assert.That(listener.Session.RemoteEndPoint, Is.Not.Null);
        });

        await listener.Session.Muxer!.Connected.WaitAsync(token);
    }

    /// <summary>
    /// The association is only reachable if <c>RTCSctpTransport.Start</c> was handed
    /// <c>dtls.Transport</c> and not <c>dtls</c> itself. Both satisfy BouncyCastle's
    /// <c>DatagramTransport</c>, so the wrong one compiles and leaves DTLS looking perfectly healthy
    /// -- the fingerprint check above still passes -- while SCTP reads undecrypted records and
    /// writes plaintext chunks, and the association silently never connects. Moving one datagram
    /// over a data channel is the cheapest assertion that fails in that case.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task Session_CarriesDataChannelTraffic_OverTheDecryptedDtlsTransport(CancellationToken token)
    {
        await using Listener listener = new();
        using Dialer dialer = new(listener.EndPoint, listener.Fingerprint);

        await listener.Session.Established.WaitAsync(token);

        // Registered before the dialer can send: the muxer routes inbound frames by stream id and
        // drops anything for an id it does not hold.
        SctpWebRtcDataChannel noise = listener.Session.Muxer!.GetNegotiatedChannel(WebRtcDirectSctpMuxer.NoiseStreamId);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        noise.MessageReceived += message => received.TrySetResult(message);

        await listener.Session.Muxer.Connected.WaitAsync(token);
        await dialer.NoiseChannelOpened.WaitAsync(token);

        byte[] payload = [0x6c, 0x69, 0x62, 0x70, 0x32, 0x70];
        dialer.NoiseChannel.send(payload);

        byte[] delivered = await received.Task.WaitAsync(token);
        Assert.That(delivered, Is.EqualTo(payload));
    }

    /// <summary>
    /// The two rules that hold before a handshake can even start, checked without a live stack so
    /// that they stay covered on a host that cannot run one: the egress is pinned to the first DTLS
    /// record's normalised source and a later source is ignored, and a session that cannot take a
    /// handshake slot allocates nothing and -- F5 -- answers nothing.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public async Task ReceiveDtls_WithNoHandshakeSlot_PinsTheFirstSourceAndSendsNothing()
    {
        (Certificate chain, AsymmetricKeyParameter privateKey) = DtlsUtils.CreateSelfSignedTlsCert(new BcTlsCrypto());
        using SemaphoreSlim exhausted = new(0, 1);
        List<IPEndPoint> sentTo = [];

        await using WebRtcDirectSession session = new(
            chain, privateKey, 4242, exhausted, (_, destination) => sentTo.Add(destination));

        // A dual-mode socket reports a v4 peer as ::ffff:a.b.c.d, which must normalise to the same
        // endpoint the v4 socket would have reported.
        byte[] record = [22, 254, 253, 0, 0];
        session.ReceiveDtls(record, new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.1"), 5000));
        session.ReceiveDtls(record, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 5001));

        Assert.Multiple(() =>
        {
            Assert.That(session.RemoteEndPoint, Is.EqualTo(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000)));
            Assert.That(sentTo, Is.Empty);
            Assert.That(session.Established.IsCompleted, Is.False);
            Assert.That(exhausted.CurrentCount, Is.Zero);
        });
    }

    /// <summary>
    /// A webrtc-direct listener's whole socket layer: one UDP port, first-byte classification per
    /// RFC 7983, and one session. No address table -- there is one peer here -- but the classify
    /// order is the real one, because answering binding requests for the life of the connection is
    /// what keeps a real dialer's consent checks alive.
    /// </summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _receiveLoop;
        private readonly SemaphoreSlim _handshakeSlots =
            new(WebRtcDirectSession.MaxConcurrentDtlsHandshakes, WebRtcDirectSession.MaxConcurrentDtlsHandshakes);

        public Listener()
        {
            (Certificate chain, AsymmetricKeyParameter privateKey) = DtlsUtils.CreateSelfSignedTlsCert(new BcTlsCrypto());
            Fingerprint = DtlsFingerprint.FromRtcFingerprint(DtlsUtils.Fingerprint(chain));

            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            EndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!;

            Session = new WebRtcDirectSession(
                chain,
                privateKey,
                EndPoint.Port,
                _handshakeSlots,
                (data, destination) => _udp.Send(data, data.Length, destination));

            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public IPEndPoint EndPoint { get; }

        public DtlsFingerprint Fingerprint { get; }

        public WebRtcDirectSession Session { get; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _udp.Dispose();

            try
            {
                await _receiveLoop;
            }
            catch (Exception)
            {
                // The loop ends by having its socket shut under it; that is the teardown, not a
                // failure of it.
            }

            await Session.DisposeAsync();
            _cts.Dispose();
            _handshakeSlots.Dispose();
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try
                {
                    packet = await _udp.ReceiveAsync(token);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    return;
                }

                // F1: the first byte decides, before any table lookup. A STUN binding request from
                // an established peer is a consent check (RFC 7675) and must still be answered.
                if (WebRtcDirectStun.IsStunDatagram(packet.Buffer))
                {
                    if (!WebRtcDirectStun.TryParseBindingRequest(packet.Buffer, out STUNMessage? request, out WebRtcIceCredentials credentials) ||
                        !WebRtcDirectStun.CheckIntegrity(request, credentials.ServerUfrag))
                    {
                        // Silently dropped: any reply to an unvalidated source is a reflector.
                        continue;
                    }

                    byte[] response = WebRtcDirectStun.BuildBindingSuccessResponse(request, packet.RemoteEndPoint, credentials.ServerUfrag);
                    _udp.Send(response, response.Length, packet.RemoteEndPoint);
                }
                else if (WebRtcDirectStun.IsDtlsRecord(packet.Buffer))
                {
                    Session.ReceiveDtls(packet.Buffer, packet.RemoteEndPoint);
                }
            }
        }
    }

    /// <summary>
    /// A dialer built the way <c>WebRtcDirectProtocol.DialAsync</c> builds one. The order matters:
    /// the negotiated id-0 data channel has to exist before <c>createOffer</c>, or SIPSorcery emits
    /// an offer with no <c>m=application</c> section at all and the answer has nothing to pair with.
    /// </summary>
    private sealed class Dialer : IDisposable
    {
        private readonly RTCPeerConnection _pc;
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dialer(IPEndPoint listenerEndPoint, DtlsFingerprint listenerFingerprint)
        {
            (Certificate chain, AsymmetricKeyParameter privateKey) = DtlsUtils.CreateSelfSignedTlsCert(new BcTlsCrypto());
            Fingerprint = DtlsFingerprint.FromRtcFingerprint(DtlsUtils.Fingerprint(chain));

            RTCCertificate2 certificate = new()
            {
                Certificate = new X509Certificate(chain.GetCertificateAt(0).GetEncoded()),
                PrivateKey = privateKey,
            };

            _pc = new RTCPeerConnection(new RTCConfiguration
            {
                // Unlike production, loopback has to be among the candidates: the listener binds
                // 127.0.0.1 so that the test never puts a packet on a real network.
                X_ICEIncludeAllInterfaceAddresses = true,
                X_GatherTimeoutMs = 2000,
                certificates2 = [certificate],
            });

            NoiseChannel = _pc.createDataChannel("noise", new RTCDataChannelInit { negotiated = true, id = 0 })
                .GetAwaiter().GetResult();
            NoiseChannel.onopen += () => _opened.TrySetResult();

            RTCSessionDescriptionInit offer = _pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
            _pc.setLocalDescription(offer).GetAwaiter().GetResult();

            // webrtc-direct has no signalling: the answer is synthesised from what a multiaddr
            // would have carried, and the server ufrag it embeds is what the listener recognises
            // the dial by.
            RTCSessionDescriptionInit answer = new()
            {
                type = RTCSdpType.answer,
                sdp = WebRtcDirectAnswerSdp.Build(listenerEndPoint, listenerFingerprint, _pc.GetRtpChannel().LocalIcePassword),
            };

            SetDescriptionResultEnum applied = _pc.setRemoteDescription(answer);
            if (applied != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"Synthetic answer rejected by the dialer: {applied}.");
            }
        }

        public DtlsFingerprint Fingerprint { get; }

        public RTCDataChannel NoiseChannel { get; }

        public Task NoiseChannelOpened => _opened.Task;

        public void Dispose()
        {
            _pc.close();
            _pc.Dispose();
        }
    }
}
