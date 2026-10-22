// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectAnswerSdpTests
{
    private const string ClientPwd = "yVLfQeq0eDYVA7t4ypAV1Z";

    private static DtlsFingerprint Fingerprint() =>
        new("sha-256", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Test]
    public void Build_Ipv4_UsesIp4TokensAndHostCandidate()
    {
        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 4242), Fingerprint(), ClientPwd);

        Assert.That(sdp, Does.Contain("\nc=IN IP4 1.2.3.4\n"));
        Assert.That(sdp, Does.Contain("\no=- 0 0 IN IP4 1.2.3.4\n"));
        Assert.That(sdp, Does.Contain("\nm=application 4242 UDP/DTLS/SCTP webrtc-datachannel\n"));
        Assert.That(sdp, Does.Contain("\na=candidate:1467250027 1 UDP 1467250027 1.2.3.4 4242 typ host\n"));
    }

    [Test]
    public void Build_Ipv6_UsesIp6TokensAndHostCandidate()
    {
        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 4242), Fingerprint(), ClientPwd);

        Assert.That(sdp, Does.Contain("\nc=IN IP6 2001:db8::1\n"));
        Assert.That(sdp, Does.Contain("\no=- 0 0 IN IP6 2001:db8::1\n"));
        Assert.That(sdp, Does.Contain("\na=candidate:1467250027 1 UDP 1467250027 2001:db8::1 4242 typ host\n"));
        Assert.That(sdp, Does.Not.Contain("IP4"));
    }

    [Test]
    public void Build_UfragAndPwdCarryV2PrefixAndClientPwd()
    {
        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Loopback, 1), Fingerprint(), ClientPwd);

        Assert.That(sdp, Does.Contain($"\na=ice-ufrag:libp2p+webrtc+v2/{ClientPwd}\n"));
        Assert.That(sdp, Does.Contain($"\na=ice-pwd:libp2p+webrtc+v2/{ClientPwd}\n"));
    }

    [Test]
    public void Build_FingerprintLineMatchesSuppliedFingerprint()
    {
        DtlsFingerprint fingerprint = Fingerprint();

        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Loopback, 1), fingerprint, ClientPwd);

        Assert.That(sdp, Does.Contain($"\na=fingerprint:{fingerprint.ToSdpString()}\n"));
        Assert.That(sdp, Does.Contain("\na=fingerprint:sha-256 00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F\n"));
    }

    [Test]
    public void Build_IsIceLitePassiveAnswerWithFixedSctpLimits()
    {
        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Loopback, 1), Fingerprint(), ClientPwd);

        Assert.That(sdp, Does.Contain("\na=ice-lite\n"));
        Assert.That(sdp, Does.Contain("\na=setup:passive\n"));
        Assert.That(sdp, Does.Contain("\na=sctp-port:5000\n"));
        Assert.That(sdp, Does.Contain("\na=max-message-size:16384\n"));
    }

    [Test]
    public void Build_UsesLfLineEndingsLikeReferenceImplementations()
    {
        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Loopback, 1), Fingerprint(), ClientPwd);

        Assert.That(sdp, Does.Not.Contain("\r"));
        Assert.That(sdp, Does.EndWith("a=end-of-candidates\n"));
    }

    [Test]
    public void Build_ProducesSdpThatSipSorceryCanParse()
    {
        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 4242), Fingerprint(), ClientPwd);

        SDP parsed = SDP.ParseSDPDescription(sdp);

        Assert.That(parsed.Media, Has.Count.EqualTo(1));
        Assert.That(parsed.Media[0].Port, Is.EqualTo(4242));
        Assert.That(parsed.Media[0].Connection.ConnectionAddress, Is.EqualTo("1.2.3.4"));
    }

    [TestCase("")]
    [TestCase("tooshort")]
    [TestCase("has:colon:and:is:long:enough")]
    [TestCase("line\nbreak\ninjection\nattempt\nxx")]
    public void Build_RejectsInvalidClientPwd(string clientPwd)
    {
        Assert.That(
            () => WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Loopback, 1), Fingerprint(), clientPwd),
            Throws.InstanceOf<ArgumentException>().Or.InstanceOf<FormatException>());
    }

    [Test]
    public void Build_GoldenSdp()
    {
        string expected = """
            v=0
            o=- 0 0 IN IP4 1.2.3.4
            s=-
            t=0 0
            a=ice-lite
            m=application 4242 UDP/DTLS/SCTP webrtc-datachannel
            c=IN IP4 1.2.3.4
            a=mid:0
            a=ice-options:ice2
            a=ice-ufrag:libp2p+webrtc+v2/yVLfQeq0eDYVA7t4ypAV1Z
            a=ice-pwd:libp2p+webrtc+v2/yVLfQeq0eDYVA7t4ypAV1Z
            a=fingerprint:sha-256 00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F
            a=setup:passive
            a=sctp-port:5000
            a=max-message-size:16384
            a=candidate:1467250027 1 UDP 1467250027 1.2.3.4 4242 typ host
            a=end-of-candidates

            """.ReplaceLineEndings("\n");

        string sdp = WebRtcDirectAnswerSdp.Build(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 4242), Fingerprint(), ClientPwd);

        Assert.That(sdp, Is.EqualTo(expected));
    }

    [TestCase(239, true)]
    [TestCase(240, false)]
    public void Build_BoundsTheUfragNotJustThePassword(int passwordLength, bool expectedValid)
    {
        // The 17 char prefix counts against the 256 char RFC 8839 ufrag limit, so the real
        // ceiling on client_pwd is 239, not 256.
        IPEndPoint endpoint = new(IPAddress.Parse("1.2.3.4"), 1234);
        DtlsFingerprint fingerprint = Fingerprint();
        string password = new('a', passwordLength);

        if (expectedValid)
        {
            string sdp = WebRtcDirectAnswerSdp.Build(endpoint, fingerprint, password);
            foreach (string line in sdp.Split('\n'))
            {
                if (line.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))
                {
                    Assert.That(line["a=ice-ufrag:".Length..].Length, Is.LessThanOrEqualTo(256));
                }
            }
        }
        else
        {
            Assert.That(() => WebRtcDirectAnswerSdp.Build(endpoint, fingerprint, password),
                Throws.InstanceOf<ArgumentException>());
        }
    }

    [Test]
    public void Build_StripsTheIPv6ScopeId()
    {
        // "fe80::1%12" is not an RFC 4566 unicast-address and means nothing to the peer.
        IPEndPoint scoped = new(IPAddress.Parse("fe80::1%12"), 4001);
        DtlsFingerprint fingerprint = Fingerprint();

        string sdp = WebRtcDirectAnswerSdp.Build(scoped, fingerprint, new string('a', 22));

        Assert.That(sdp, Does.Not.Contain("%"));
        Assert.That(sdp, Does.Contain("c=IN IP6 fe80::1\n"));
        Assert.That(sdp, Does.Contain("a=candidate:1467250027 1 UDP 1467250027 fe80::1 4001 typ host"));
    }
}
