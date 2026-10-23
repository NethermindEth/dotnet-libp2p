// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Buffers.Binary;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// The Noise handshake over the real transport path: two <see cref="WebRtcStream"/>s wired back to
/// back, so what crosses the wire is <c>webrtc.pb.Message</c> frames rather than the raw Noise
/// bytes the dial side used to write.
/// <para>
/// webrtc-direct.md, Connection Security: the handshake runs on the negotiated id-0 channel "using
/// the message framing described in the Multiplexing section". Unframed, it interoperates with no
/// other implementation -- and a self-dial cannot see that, because both halves agree on the wrong
/// thing. Hence <see cref="Handshake_PutsOnlyFramedMessagesOnTheWire"/>, whose oracle is
/// <see cref="WebRtcFraming"/> decoding the wire, not the other side being happy.
/// </para>
/// </summary>
[TestFixture]
public class WebRtcNoiseOverStreamTests
{
    /// <summary>Noise XX message 1 is "-> e": one X25519 public key, nothing else.</summary>
    private const int NoiseMessageOneLength = 32;

    /// <summary>
    /// Distinguishable on purpose: a prologue built from the wrong fingerprint order still agrees
    /// with itself when both sides hold the same bytes, so the two must differ for
    /// <see cref="WebRtcDirectRoles.OrderFingerprints"/> to be doing anything here.
    /// </summary>
    private static readonly DtlsFingerprint DialerFingerprint =
        new("sha-256", [.. Enumerable.Repeat((byte)0xAA, 32)]);

    private static readonly DtlsFingerprint ListenerFingerprint =
        new("sha-256", [.. Enumerable.Repeat((byte)0xBB, 32)]);

    [Test]
    [CancelAfter(30_000)]
    public async Task Handshake_OverFramedStreams_AuthenticatesBothPeers(CancellationToken token)
    {
        Handshake handshake = Handshake.Start();

        (IChannel dialerEncrypted, PublicKey dialerSaw) = await handshake.Dialer.WaitAsync(token);
        (IChannel listenerEncrypted, PublicKey listenerSaw) = await handshake.Listener.WaitAsync(token);

        Assert.Multiple(() =>
        {
            Assert.That(new Identity(dialerSaw).PeerId, Is.EqualTo(handshake.ListenerIdentity.PeerId),
                "The dialer must recover the listener's peer ID.");
            Assert.That(new Identity(listenerSaw).PeerId, Is.EqualTo(handshake.DialerIdentity.PeerId),
                "The listener must recover the dialer's peer ID.");
        });

        await dialerEncrypted.CloseAsync();
        await listenerEncrypted.CloseAsync();
    }

    /// <summary>
    /// The point of the work item. Every byte each side handed its data channel is decoded back as
    /// a sequence of whole <c>webrtc.pb.Message</c> frames with nothing left over, and the payloads
    /// those frames carry -- not the wire bytes -- are the Noise stream. On the old unframed path
    /// the Noise bytes went out as-is and this fails.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Handshake_PutsOnlyFramedMessagesOnTheWire(CancellationToken token)
    {
        Handshake handshake = Handshake.Start();
        (IChannel dialerEncrypted, _) = await handshake.Dialer.WaitAsync(token);
        (IChannel listenerEncrypted, _) = await handshake.Listener.WaitAsync(token);

        (byte[] listenerWire, byte[] listenerPayload) = Unframe(handshake.ListenerChannel.SentFrames);
        (byte[] dialerWire, byte[] dialerPayload) = Unframe(handshake.DialerChannel.SentFrames);
        List<int> listenerRecords = NoiseRecordLengths(listenerPayload);
        List<int> dialerRecords = NoiseRecordLengths(dialerPayload);

        Assert.Multiple(() =>
        {
            // The listener is _B_, the Noise initiator: it writes message 1 and message 3.
            Assert.That(listenerRecords, Has.Count.EqualTo(2));
            Assert.That(listenerRecords.FirstOrDefault(), Is.EqualTo(NoiseMessageOneLength),
                "Noise XX message 1 is a bare ephemeral key, so the first thing inside the frames is 32 bytes.");

            // The dialer is _A_, the Noise responder: message 2 and nothing else.
            Assert.That(dialerRecords, Has.Count.EqualTo(1));

            // Framing is additive, so wire > payload on both sides. Equal lengths would mean the
            // Noise bytes went out untouched, which is exactly the bug.
            Assert.That(listenerWire, Has.Length.GreaterThan(listenerPayload.Length));
            Assert.That(dialerWire, Has.Length.GreaterThan(dialerPayload.Length));
        });

        await dialerEncrypted.CloseAsync();
        await listenerEncrypted.CloseAsync();
    }

    /// <summary>
    /// Decodes everything one side sent, asserting as it goes that the wire is a clean sequence of
    /// whole frames, and returns the raw wire bytes next to the payload they carried.
    /// </summary>
    private static (byte[] Wire, byte[] Payload) Unframe(IReadOnlyList<byte[]> sentFrames)
    {
        byte[] wire = [.. sentFrames.SelectMany(frame => frame)];
        Assert.That(wire, Is.Not.Empty, "Nothing reached the data channel.");

        List<byte> payload = [];
        int offset = 0;
        while (offset < wire.Length)
        {
            bool decoded = WebRtcFraming.TryDecode(wire.AsSpan(offset), out Message? message, out int consumed);
            Assert.That(decoded, Is.True, $"Bytes at offset {offset} are not a complete webrtc.pb.Message frame.");
            Assert.That(message!.HasFlag, Is.False, "The handshake sends no FIN/STOP_SENDING/RESET_STREAM flags.");
            Assert.That(message.HasMessage_, Is.True, "Every handshake frame carries payload.");

            payload.AddRange(message.Message_.ToByteArray());
            offset += consumed;
        }

        return (wire, [.. payload]);
    }

    /// <summary>
    /// Splits the unframed payload back into the handshake's own 2-byte big-endian length-prefixed
    /// records, which is how <see cref="WebRtcDirectNoiseHandshake"/> delimits Noise messages
    /// inside the stream. Throws rather than returning junk if the payload does not line up, so a
    /// payload that is not actually the Noise stream fails the test.
    /// </summary>
    private static List<int> NoiseRecordLengths(byte[] payload)
    {
        List<int> lengths = [];
        int offset = 0;
        while (offset < payload.Length)
        {
            Assert.That(payload.Length - offset, Is.GreaterThanOrEqualTo(2), "Truncated Noise length prefix.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
            offset += 2;
            Assert.That(payload.Length - offset, Is.GreaterThanOrEqualTo(length), "Truncated Noise message.");
            lengths.Add(length);
            offset += length;
        }

        return lengths;
    }

    /// <summary>
    /// Both halves of one conversation: a pair of in-memory data channels, a
    /// <see cref="WebRtcStream"/> and <see cref="WebRtcStreamChannel"/> on each, and the two
    /// handshakes already running against each other.
    /// </summary>
    private sealed record Handshake(
        FakeWebRtcDataChannel DialerChannel,
        FakeWebRtcDataChannel ListenerChannel,
        Identity DialerIdentity,
        Identity ListenerIdentity,
        Task<(IChannel Encrypted, PublicKey RemoteKey)> Dialer,
        Task<(IChannel Encrypted, PublicKey RemoteKey)> Listener)
    {
        public static Handshake Start()
        {
            (FakeWebRtcDataChannel dialerChannel, FakeWebRtcDataChannel listenerChannel) = FakeWebRtcDataChannel.CreatePair();
            WebRtcStreamChannel dialerSide = new(new WebRtcStream(dialerChannel));
            WebRtcStreamChannel listenerSide = new(new WebRtcStream(listenerChannel));

            Identity dialer = new();
            Identity listener = new();

            // Each side passes its OWN fingerprint as local, and OrderFingerprints puts the pair
            // into the (_A_, _B_) order the prologue wants. The two calls take the arguments the
            // other way round on purpose: that is the whole thing being exercised.
            (DtlsFingerprint a, DtlsFingerprint b) asDialer = WebRtcDirectRoles.OrderFingerprints(
                isListener: false, local: DialerFingerprint, remote: ListenerFingerprint);
            (DtlsFingerprint a, DtlsFingerprint b) asListener = WebRtcDirectRoles.OrderFingerprints(
                isListener: true, local: ListenerFingerprint, remote: DialerFingerprint);

            // The responder is started first so it is already parked on its read when the
            // initiator's first frame is delivered -- FakeWebRtcDataChannel delivers inline.
            Task<(IChannel, PublicKey)> dialerHandshake = WebRtcDirectNoiseHandshake.HandshakeAsync(
                dialerSide,
                dialer,
                WebRtcNoisePrologue.Build(asDialer.a, asDialer.b),
                isInitiator: !WebRtcDirectRoles.ListenerIsNoiseInitiator,
                CancellationToken.None);
            Task<(IChannel, PublicKey)> listenerHandshake = WebRtcDirectNoiseHandshake.HandshakeAsync(
                listenerSide,
                listener,
                WebRtcNoisePrologue.Build(asListener.a, asListener.b),
                isInitiator: WebRtcDirectRoles.ListenerIsNoiseInitiator,
                CancellationToken.None);

            return new Handshake(dialerChannel, listenerChannel, dialer, listener, dialerHandshake, listenerHandshake);
        }
    }
}
