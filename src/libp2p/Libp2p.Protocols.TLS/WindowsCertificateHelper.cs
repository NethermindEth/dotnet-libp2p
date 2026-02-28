// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using System.Formats.Asn1;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// Windows-compatible certificate helper that avoids Windows Certificate Store issues
/// </summary>
public class WindowsCertificateHelper
{
    private const string PubkeyExtensionOidString = "1.3.6.1.4.1.53594.1.1";
    private static readonly Oid PubkeyExtensionOid = new(PubkeyExtensionOidString);

    /// <summary>
    /// Creates a certificate from identity without using Windows Certificate Store
    /// </summary>
    public static X509Certificate2 CreateCertificateFromIdentity(ECDsa sessionKey, Identity identity)
    {
        try
        {
            byte[] signature = identity.Sign(ContentToSignFromTlsPublicKey(sessionKey.ExportSubjectPublicKeyInfo()));

            AsnWriter asnWriter = new(AsnEncodingRules.DER);
            asnWriter.PushSequence();
            asnWriter.WriteOctetString(identity.PublicKey.ToByteArray());
            asnWriter.WriteOctetString(signature);
            asnWriter.PopSequence();
            byte[] pubkeyExtension = new byte[asnWriter.GetEncodedLength()];
            asnWriter.Encode(pubkeyExtension);

            // Use a deterministic subject name to avoid randomness issues
            string subjectName = $"CN=libp2p-{identity.PeerId}";
            CertificateRequest certRequest = new(subjectName, sessionKey, HashAlgorithmName.SHA256);

            certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, true));

            // Create certificate without using Windows Certificate Store
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1); // Small buffer for clock skew
            var notAfter = DateTimeOffset.UtcNow.AddYears(1); // Valid for 1 year

            X509Certificate2 result = certRequest.CreateSelfSigned(notBefore, notAfter);

            // On Windows, we need to ensure the certificate has a private key
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Export and re-import to ensure private key is properly associated
                byte[] pfxBytes = result.Export(X509ContentType.Pfx);
                result.Dispose();
                result = new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
            }

            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create certificate from identity: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates certificate with improved private key handling for Windows
    /// </summary>
    public static X509Certificate2 CreateCertificateWithPrivateKey(ECDsa sessionKey, Identity identity)
    {
        try
        {
            X509Certificate2 cert = CreateCertificateFromIdentity(sessionKey, identity);

            // Ensure the certificate has the private key properly associated
            if (!cert.HasPrivateKey && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Create a new certificate that combines the certificate with the private key
                using (cert)
                {
                    return cert.CopyWithPrivateKey(sessionKey);
                }
            }

            return cert;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create certificate with private key: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Validates certificate without relying on Windows Certificate Store
    /// </summary>
    public static bool ValidateCertificate(X509Certificate2? certificate, string? peerId)
    {
        if (certificate is null)
        {
            return false;
        }

        try
        {
            X509Extension[] exts = certificate.Extensions.Where(e => e.Oid?.Value == PubkeyExtensionOidString).ToArray();

            if (exts.Length != 1)
            {
                return false;
            }

            X509Extension ext = exts.First();

            AsnReader reader = new(ext.RawData, AsnEncodingRules.DER);
            AsnReader signedKey = reader.ReadSequence();

            byte[] publicKey = signedKey.ReadOctetString();
            byte[] signature = signedKey.ReadOctetString();

            Core.Dto.PublicKey key = Core.Dto.PublicKey.Parser.ParseFrom(publicKey);
            Identity id = new(key);

            if (peerId is not null && id.PeerId.ToString() != peerId)
            {
                return false;
            }

            return id.VerifySignature(ContentToSignFromTlsPublicKey(certificate.PublicKey.ExportSubjectPublicKeyInfo()), signature);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates ECDSA key with Windows compatibility
    /// </summary>
    public static ECDsa CreateWindowsCompatibleECDsa()
    {
        try
        {
            // Try to create using the standard P-256 curve
            return ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }
        catch
        {
            // Fallback for older Windows versions
            try
            {
                var ecdsa = ECDsa.Create();
                // Generate a new key pair using the default curve
                ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
                return ecdsa;
            }
            catch
            {
                // Last resort - use the basic create method
                return ECDsa.Create();
            }
        }
    }

    private static readonly byte[] SignaturePrefix = "libp2p-tls-handshake:"u8.ToArray();
    private static byte[] ContentToSignFromTlsPublicKey(byte[] keyInfo) => [.. SignaturePrefix, .. keyInfo];
}
