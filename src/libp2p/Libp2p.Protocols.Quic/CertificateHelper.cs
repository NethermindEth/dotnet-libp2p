// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Org.BouncyCastle.X509;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.Quic.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.Tls")]

namespace Nethermind.Libp2p.Protocols.Quic;

public class CertificateHelper
{
    private const string PubkeyExtensionOidString = "1.3.6.1.4.1.53594.1.1";
    private static readonly Oid PubkeyExtensionOid = new(PubkeyExtensionOidString);

    /// <summary>
    /// Creates a libp2p certificate. On Windows the certificate uses a persisted named CNG key;
    /// callers must delete that key after the certificate's last TLS use.
    /// </summary>
    public static X509Certificate2 CertificateFromIdentity(ECDsa sessionKey, Identity identity)
    {
        // On Windows, SslStream (SChannel) requires a named CNG key.
        // Ephemeral keys from ECDsa.Create() cannot be used with SslStream on Windows.
        ECDsa certKey = sessionKey;
        if (OperatingSystem.IsWindows())
        {
            certKey = CreateWindowsCompatibleKey();
        }

        try
        {
            return CreateCertificate(certKey, identity);
        }
        catch
        {
            if (certKey is ECDsaCng cngKey)
            {
                using CngKey key = cngKey.Key;
                key.Delete();
            }
            throw;
        }
        finally
        {
            if (!ReferenceEquals(certKey, sessionKey))
            {
                certKey.Dispose();
            }
        }
    }

    internal static CertificateLease CreateCertificateLease(ECDsa sessionKey, Identity identity) =>
        new(CertificateFromIdentity(sessionKey, identity));

    internal sealed class CertificateLease(X509Certificate2 certificate) : IDisposable
    {
        public X509Certificate2 Certificate { get; } = certificate;

        public void Dispose()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using ECDsa? privateKey = Certificate.GetECDsaPrivateKey();
                    if (privateKey is ECDsaCng cngKey)
                    {
                        using CngKey key = cngKey.Key;
                        key.Delete();
                    }
                }
            }
            finally
            {
                Certificate.Dispose();
            }
        }
    }

    private static X509Certificate2 CreateCertificate(ECDsa certKey, Identity identity)
    {
        Span<byte> signature = identity.Sign(ContentToSignFromTlsPublicKey(certKey.ExportSubjectPublicKeyInfo()));
        AsnWriter asnWriter = new(AsnEncodingRules.DER);
        asnWriter.PushSequence();
        asnWriter.WriteOctetString(identity.PublicKey.ToByteArray());
        asnWriter.WriteOctetString(signature);
        asnWriter.PopSequence();

        Span<byte> pubkeyExtension = stackalloc byte[asnWriter.GetEncodedLength()];
        int d = asnWriter.Encode(pubkeyExtension);

        Span<byte> bytes = stackalloc byte[20];
        Random random = new();
        random.NextBytes(bytes);

        CertificateRequest certRequest = new($"SERIALNUMBER={Convert.ToHexString(bytes)}", certKey, HashAlgorithmName.SHA256);

        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, critical: false));

        // Per libp2p TLS spec: NotAfter must not be later than 2^63-1 seconds from Unix epoch (~year 2292).
        // Using a 100-year validity to stay within spec and avoid ASN.1 parsing issues in other implementations.
        return certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(100));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ECDsa CreateWindowsCompatibleKey()
    {
        CngKeyCreationParameters cngParams = new()
        {
            ExportPolicy = CngExportPolicies.AllowPlaintextExport,
            KeyUsage = CngKeyUsages.AllUsages,
        };
        using CngKey cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, $"libp2p-{Guid.NewGuid():N}", cngParams);
        return new ECDsaCng(cngKey);
    }

    public static bool ValidateCertificate(X509Certificate2 certificate, string? peerId) =>
        ValidateCertificate(certificate, peerId, out _);

    internal static bool ValidateCertificate(X509Certificate2 certificate, string? peerId, [NotNullWhen(false)] out string? failureReason)
    {
        failureReason = null;

        // Per libp2p TLS spec: Check certificate validity dates
        DateTime now = DateTime.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now)
        {
            failureReason = "certificate is not yet valid";
            return false; // Certificate not yet valid
        }
        if (certificate.NotAfter.ToUniversalTime() < now)
        {
            failureReason = "certificate expired";
            return false; // Certificate expired
        }

        if (!HasValidSelfSignature(certificate))
        {
            failureReason = "certificate self-signature is invalid";
            return false; // Certificate self-signature is invalid
        }

        try
        {
            foreach (X509Extension extension in certificate.Extensions)
            {
                if (!extension.Critical)
                {
                    continue;
                }

                switch (extension.Oid?.Value)
                {
                    case PubkeyExtensionOidString:
                    case "2.5.29.19" when extension is X509BasicConstraintsExtension { CertificateAuthority: false }:
                    case "2.5.29.15" when extension is X509KeyUsageExtension keyUsage
                        && (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0:
                        break;
                    default:
                        failureReason = "certificate contains an unknown or invalid critical extension";
                        return false;
                }
            }
        }
        catch (CryptographicException)
        {
            failureReason = "certificate contains a malformed critical extension";
            return false;
        }

        Core.Dto.PublicKey? key = ExtractPublicKey(certificate, out byte[]? signature);

        if (key is null || signature is null)
        {
            failureReason = "libp2p public key extension is missing or invalid";
            return false; // Missing libp2p extension or signature
        }

        // Verify the signature over the certificate's subjectPublicKeyInfo.
        // Parse it from the certificate DER instead of going through
        // X509Certificate2.PublicKey, which does not cover every key type used
        // by libp2p QUIC peers.
        byte[]? subjectPublicKeyInfo = ReadSubjectPublicKeyInfo(certificate);
        if (subjectPublicKeyInfo is null)
        {
            failureReason = "certificate subject public key info is malformed";
            return false; // Malformed certificate body
        }

        try
        {
            Identity id = new(key);
            if (peerId is not null && id.PeerId.ToString() != peerId)
            {
                failureReason = "peer id does not match certificate public key";
                return false; // Peer ID mismatch
            }

            if (!id.VerifySignature(ContentToSignFromTlsPublicKey(subjectPublicKeyInfo), signature))
            {
                failureReason = "libp2p public key extension signature is invalid";
                return false;
            }
        }
        catch (Exception)
        {
            failureReason = "libp2p public key extension is invalid";
            return false;
        }

        return true;
    }

    public static Core.Dto.PublicKey? ExtractPublicKey(X509Certificate2? certificate, [NotNullWhen(true)] out byte[]? signature)
    {
        signature = null;

        if (certificate is null)
        {
            return null;
        }

        // Per libp2p TLS spec: Find the libp2p extension
        X509Extension[] exts = certificate.Extensions.Where(e => e.Oid?.Value == PubkeyExtensionOidString).ToArray();

        if (exts.Length == 0)
        {
            return null; // libp2p extension missing
        }

        if (exts.Length > 1)
        {
            return null; // Multiple libp2p extensions not allowed
        }

        X509Extension ext = exts.First();

        try
        {
            AsnReader a = new(ext.RawData, AsnEncodingRules.DER);
            AsnReader signedKey = a.ReadSequence();

            byte[] publicKey = signedKey.ReadOctetString();
            signature = signedKey.ReadOctetString();

            return Core.Dto.PublicKey.Parser.ParseFrom(publicKey);
        }
        catch
        {
            // Invalid ASN.1 structure
            signature = null;
            return null;
        }
    }

    private static readonly byte[] SignaturePrefix = "libp2p-tls-handshake:"u8.ToArray();
    private static byte[] ContentToSignFromTlsPublicKey(byte[] keyInfo) => [.. SignaturePrefix, .. keyInfo];

    private static bool HasValidSelfSignature(X509Certificate2 certificate)
    {
        try
        {
            X509CertificateParser parser = new();
            Org.BouncyCastle.X509.X509Certificate parsedCertificate = parser.ReadCertificate(certificate.RawData);
            parsedCertificate.Verify(parsedCertificate.GetPublicKey());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ReadSubjectPublicKeyInfo(X509Certificate2 certificate)
    {
        try
        {
            AsnReader certificateReader = new(certificate.RawData, AsnEncodingRules.DER);
            AsnReader certificateSequence = certificateReader.ReadSequence();
            AsnReader tbsCertificate = certificateSequence.ReadSequence();

            if (tbsCertificate.PeekTag() is { TagClass: TagClass.ContextSpecific, TagValue: 0 })
            {
                tbsCertificate.ReadEncodedValue();
            }

            tbsCertificate.ReadEncodedValue(); // serialNumber
            tbsCertificate.ReadEncodedValue(); // signature
            tbsCertificate.ReadEncodedValue(); // issuer
            tbsCertificate.ReadEncodedValue(); // validity
            tbsCertificate.ReadEncodedValue(); // subject

            return tbsCertificate.ReadEncodedValue().ToArray();
        }
        catch
        {
            // Invalid ASN.1 structure
            return null;
        }
    }
}
