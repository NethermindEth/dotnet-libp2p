// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectSignalingTests
{
    [Test]
    public void OfferEnvelope_Roundtrip_ValidatesSignatureAndIdentity()
    {
        Identity signer = new();
        string sessionId = WebRtcDirectSignaling.NewSessionId();
        string sdp = "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n";

        string wire = WebRtcDirectSignaling.BuildSignedPayload(WebRtcDirectSignalType.Offer, signer, sessionId, sdp);
        WebRtcDirectReplayWindow replayWindow = new();

        (string parsedSessionId, string parsedSdp, Identity parsedSigner) = WebRtcDirectSignaling.ParseAndValidate(
            wire,
            WebRtcDirectSignalType.Offer,
            sessionId,
            replayWindow);

        Assert.That(parsedSessionId, Is.EqualTo(sessionId));
        Assert.That(parsedSdp, Is.EqualTo(sdp));
        Assert.That(parsedSigner.PeerId, Is.EqualTo(signer.PeerId));
    }

    [Test]
    public void OfferEnvelope_Replay_IsRejected()
    {
        Identity signer = new();
        string sessionId = WebRtcDirectSignaling.NewSessionId();
        string wire = WebRtcDirectSignaling.BuildSignedPayload(WebRtcDirectSignalType.Offer, signer, sessionId, "v=0");
        WebRtcDirectReplayWindow replayWindow = new();

        _ = WebRtcDirectSignaling.ParseAndValidate(wire, WebRtcDirectSignalType.Offer, sessionId, replayWindow);

        Assert.That(
            () => WebRtcDirectSignaling.ParseAndValidate(wire, WebRtcDirectSignalType.Offer, sessionId, replayWindow),
            Throws.TypeOf<CryptographicException>());
    }

    [Test]
    public void AnswerEnvelope_SessionMismatch_IsRejected()
    {
        Identity signer = new();
        string wire = WebRtcDirectSignaling.BuildSignedPayload(WebRtcDirectSignalType.Answer, signer, "session-a", "v=0");
        WebRtcDirectReplayWindow replayWindow = new();

        Assert.That(
            () => WebRtcDirectSignaling.ParseAndValidate(wire, WebRtcDirectSignalType.Answer, "session-b", replayWindow),
            Throws.TypeOf<FormatException>());
    }

    [Test]
    public void Envelope_TamperedSignature_IsRejected()
    {
        Identity signer = new();
        string sessionId = WebRtcDirectSignaling.NewSessionId();
        string wire = WebRtcDirectSignaling.BuildSignedPayload(WebRtcDirectSignalType.Offer, signer, sessionId, "v=0");

        Dictionary<string, JsonElement>? payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(wire);
        Assert.That(payload, Is.Not.Null);
        string signature = payload!["signature"].GetString()!;
        char replacement = signature[^1] == 'A' ? 'B' : 'A';
        payload["signature"] = JsonDocument.Parse($"\"{signature[..^1]}{replacement}\"").RootElement.Clone();
        string tamperedWire = JsonSerializer.Serialize(payload);

        Assert.That(
            () => WebRtcDirectSignaling.ParseAndValidate(tamperedWire, WebRtcDirectSignalType.Offer, sessionId, new WebRtcDirectReplayWindow()),
            Throws.TypeOf<CryptographicException>());
    }
}
