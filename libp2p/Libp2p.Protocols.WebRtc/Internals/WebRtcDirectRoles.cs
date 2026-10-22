// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// Every bit of this transport that differs between the dialer (_A_) and the listener (_B_),
/// gathered into one screen a reviewer can hold against the spec.
/// <para>
/// This is containment, not coverage. Each value below is a bare constant, so a test asserting one
/// against itself would be a tautology and prove nothing; what makes them trustworthy is tests
/// whose oracle lives outside this file -- the spec's own published prologue vector in
/// <c>WebRtcNoisePrologueTests.Build_MatchesTheSpecTestVector</c>, and the Noise specification's
/// message-one size in
/// <c>WebRtcDirectNoiseHandshakeTests.Initiator_WritesNoiseMessageOne_BeforeReadingAnything</c>.
/// What this file buys is that the flags stop being invisible: the Noise role was inverted on both
/// sides of this transport at once and no test saw it, because a self-dial gets both halves wrong
/// together and they cancel out.
/// </para>
/// </summary>
internal static class WebRtcDirectRoles
{
    /// <summary>
    /// webrtc-direct.md, Connection Security step 2: "_B_ starts a Noise XX handshake on the new
    /// channel". The FAQ entry "Why does B start the Noise handshake and not A?" gives the reason:
    /// WebRTC uses DTLS 1.2, so _B_, the DTLS server, is the side that may send first.
    /// </summary>
    public const bool ListenerIsNoiseInitiator = true;

    /// <summary>
    /// The listener answers with <c>a=setup:passive</c> (see <see cref="WebRtcDirectAnswerSdp"/>),
    /// which makes the dialer the DTLS client and the listener the DTLS server. Both
    /// <see cref="ListenerIsNoiseInitiator"/> and the stream id parity below are consequences of
    /// this one fact, so it is the one to check first when a role looks wrong.
    /// </summary>
    public const bool ListenerIsDtlsClient = false;

    /// <summary>
    /// Orders a local/remote fingerprint pair into the (dialer, listener) order
    /// <see cref="WebRtcNoisePrologue.Build"/> requires: the prologue is
    /// "libp2p-webrtc-noise:" + multihash(_A_) + multihash(_B_), _A_ first, whichever side builds
    /// it. Both sides pass their own fingerprint as <paramref name="local"/>, so the argument order
    /// stops being something each call site has to get right on its own.
    /// </summary>
    public static (DtlsFingerprint Dialer, DtlsFingerprint Listener) OrderFingerprints(
        bool isListener,
        DtlsFingerprint local,
        DtlsFingerprint remote)
        => isListener ? (remote, local) : (local, remote);

    /// <summary>
    /// RFC 8832 section 6: the DTLS client must choose even stream identifiers and the DTLS server
    /// odd ones. Zero is even and so falls in the client's half, but libp2p reserves it for the
    /// Noise channel, which is why the client starts at 2. Must stay equal to the seed
    /// <see cref="WebRtcDirectSctpMuxer"/> gives <c>_nextOutboundStreamId</c> in its constructor
    /// (WebRtcDirectSctpMuxer.cs:85 as of this commit).
    /// </summary>
    public static ushort FirstOutboundStreamId(bool isDtlsClient) => (ushort)(isDtlsClient ? 2 : 1);
}
