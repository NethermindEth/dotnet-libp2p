// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class CertificateHelperTests
{
    [Test]
    public void GeneratedCertificate_ProducesValidSha256Fingerprint()
    {
        using var cert = CertificateHelper.GenerateSelfSignedCertificate();
        DtlsFingerprint fp = CertificateHelper.GetFingerprint(cert);

        Assert.That(fp.Algorithm, Is.EqualTo("sha-256"));
        Assert.That(fp.Value.Length, Is.EqualTo(32));
        Assert.That(CertificateHelper.ValidateRemoteFingerprint(cert, fp), Is.True);
    }
}