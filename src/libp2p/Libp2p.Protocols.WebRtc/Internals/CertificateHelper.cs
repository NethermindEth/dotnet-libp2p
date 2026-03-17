// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal static class CertificateHelper
{
    public static X509Certificate2 GenerateSelfSignedCertificate()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=libp2p-webrtc-direct", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public static DtlsFingerprint GetFingerprint(X509Certificate2 certificate)
    {
        byte[] digest = SHA256.HashData(certificate.RawData);
        return new DtlsFingerprint("sha-256", digest);
    }

    public static bool ValidateRemoteFingerprint(X509Certificate2 remoteCert, DtlsFingerprint expected)
    {
        DtlsFingerprint actual = GetFingerprint(remoteCert);
        return actual.Algorithm.Equals(expected.Algorithm, StringComparison.OrdinalIgnoreCase) &&
               CryptographicOperations.FixedTimeEquals(actual.Value, expected.Value);
    }
}