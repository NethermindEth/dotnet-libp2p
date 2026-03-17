// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectSdpTests
{
    [Test]
    public void BuildOffer_ContainsSingleApplicationSectionAndHostCandidate()
    {
        IPEndPoint endpoint = new(IPAddress.Parse("127.0.0.1"), 9090);
        DtlsFingerprint local = new("sha-256", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        RTCSessionDescriptionInit offer = WebRtcDirectSdp.BuildOffer(endpoint, local);

        Assert.That(offer.type, Is.EqualTo(RTCSdpType.offer));
        Assert.That(CountLines(offer.sdp!, "m=application "), Is.EqualTo(1));
        Assert.That(offer.sdp, Does.Contain("a=candidate:1 1 udp").And.Contain("127.0.0.1 9090 typ host"));
        Assert.That(offer.sdp, Does.Contain("a=setup:active"));
        Assert.That(offer.sdp, Does.Contain($"a=fingerprint:{local.ToSdpString()}"));
    }

    [Test]
    public void BuildAnswer_IsPassive()
    {
        IPEndPoint endpoint = new(IPAddress.Parse("127.0.0.1"), 9090);
        DtlsFingerprint local = new("sha-256", Enumerable.Range(32, 32).Select(i => (byte)i).ToArray());

        RTCSessionDescriptionInit offer = WebRtcDirectSdp.BuildOffer(endpoint, local);
        RTCSessionDescriptionInit answer = WebRtcDirectSdp.BuildAnswer(offer, local);

        Assert.That(answer.type, Is.EqualTo(RTCSdpType.answer));
        Assert.That(answer.sdp, Does.Contain("a=setup:passive"));
    }

    [Test]
    public void ExtractFingerprint_ReturnsOfferFingerprint()
    {
        IPEndPoint endpoint = new(IPAddress.Parse("127.0.0.1"), 9090);
        DtlsFingerprint expected = new("sha-256", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

        RTCSessionDescriptionInit offer = WebRtcDirectSdp.BuildOffer(endpoint, expected);
        DtlsFingerprint parsed = WebRtcDirectSdp.ExtractFingerprint(offer.sdp!);

        Assert.That(parsed.Algorithm, Is.EqualTo(expected.Algorithm));
        Assert.That(parsed.Value, Is.EqualTo(expected.Value));
    }

    [Test]
    public void ExtractFingerprint_ThrowsWhenMissingFingerprintLine()
    {
        string sdpWithoutFingerprint = string.Join("\r\n", new[]
        {
            "v=0",
            "o=- 0 0 IN IP4 127.0.0.1",
            "s=-",
            "t=0 0",
            "m=application 9090 UDP/DTLS/SCTP webrtc-datachannel",
            string.Empty,
        });

        Assert.Throws<FormatException>(() => WebRtcDirectSdp.ExtractFingerprint(sdpWithoutFingerprint));
    }

    private static int CountLines(string sdp, string prefix)
        => sdp.Split("\r\n", StringSplitOptions.None).Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
}