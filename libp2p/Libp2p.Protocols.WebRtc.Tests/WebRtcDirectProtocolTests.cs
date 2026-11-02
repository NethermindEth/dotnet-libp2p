// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectProtocolTests
{
    private static readonly DtlsFingerprint Expected = new("sha-256", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [TestCase("sha-256")]
    [TestCase("SHA-256")]
    public void ValidateRemoteFingerprint_AcceptsMatchingCertificate(string algorithm)
    {
        SIPSorcery.Net.RTCDtlsFingerprint remote = new() { algorithm = algorithm, value = Expected.ToSdpString().Split(' ')[1] };
        Assert.That(() => WebRtcDirectProtocol.ValidateRemoteFingerprint(remote, Expected), Throws.Nothing);
    }

    [TestCase("null")]
    [TestCase("digest")]
    [TestCase("algorithm")]
    public void ValidateRemoteFingerprint_RejectsUnboundCertificate(string change)
    {
        SIPSorcery.Net.RTCDtlsFingerprint? remote = change == "null" ? null : new()
        {
            algorithm = change == "algorithm" ? "sha-384" : Expected.Algorithm,
            value = (change == "digest" ? Expected with { Value = new byte[32] } : Expected).ToSdpString().Split(' ')[1]
        };
        Assert.That(() => WebRtcDirectProtocol.ValidateRemoteFingerprint(remote, Expected),
            Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("Remote DTLS fingerprint does not match /certhash."));
    }

    [Test]
    public void Constructor_CreatesLocalDtlsCertificate()
    {
        Assert.That(() => new WebRtcDirectProtocol(), Throws.Nothing);
    }
}
