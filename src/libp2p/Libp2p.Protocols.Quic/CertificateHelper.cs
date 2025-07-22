// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.Quic;

public class CertificateHelper
{
    private const string PubkeyExtensionOidString = "1.3.6.1.4.1.53594.1.1";
    private static readonly Oid PubkeyExtensionOid = new(PubkeyExtensionOidString);

    public static X509Certificate2 CertificateFromIdentity(ECDsa sessionKey, Identity identity)
    {
        Span<byte> signature = identity.Sign(ContentToSignFromTlsPublicKey(sessionKey.ExportSubjectPublicKeyInfo()));
        AsnWriter asnWrtier = new(AsnEncodingRules.DER);
        asnWrtier.PushSequence();
        asnWrtier.WriteOctetString(identity.PublicKey.ToByteArray());
        asnWrtier.WriteOctetString(signature);
        asnWrtier.PopSequence();

        Span<byte> pubkeyExtension = stackalloc byte[asnWrtier.GetEncodedLength()];
        int d = asnWrtier.Encode(pubkeyExtension);

        Span<byte> bytes = stackalloc byte[20];
        Random random = new();
        random.NextBytes(bytes);

        CertificateRequest certRequest = new($"SERIALNUMBER={Convert.ToHexString(bytes)}", sessionKey, HashAlgorithmName.SHA256);

        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, false));

        X509Certificate2 certificate = certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.MaxValue);

        return certificate;
    }

    public static bool ValidateCertificate(X509Certificate2 certificate, string? peerId)
    {
        Core.Dto.PublicKey? key = ExtractPublicKey(certificate, out byte[]? signature);

        if (key is null || signature is null)
        {
            return false;
        }
        Identity id = new(key);
        if (peerId is not null && id.PeerId.ToString() != peerId)
        {
            return false;
        }

        return id.VerifySignature(ContentToSignFromTlsPublicKey(certificate.PublicKey.ExportSubjectPublicKeyInfo()), signature);
    }

    public static Core.Dto.PublicKey? ExtractPublicKey(X509Certificate2? certificate, [NotNullWhen(true)] out byte[]? signature)
    {
        if (certificate is null)
        {
            signature = null;
            return null;
        }

        X509Extension[] exts = certificate.Extensions.Where(e => e.Oid?.Value == PubkeyExtensionOidString).ToArray();

        if (exts.Length is 0)
        {
            signature = null;
            return null;
        }

        if (exts.Length is not 1)
        {
            signature = null;
            return null;
        }

        X509Extension ext = exts.First();

        AsnReader a = new(ext.RawData, AsnEncodingRules.DER);
        AsnReader signedKey = a.ReadSequence();

        byte[] publicKey = signedKey.ReadOctetString();
        signature = signedKey.ReadOctetString();

        return Core.Dto.PublicKey.Parser.ParseFrom(publicKey);
    }

    private static readonly byte[] SignaturePrefix = "libp2p-tls-handshake:"u8.ToArray();
    private static byte[] ContentToSignFromTlsPublicKey(byte[] keyInfo) => [.. SignaturePrefix, .. keyInfo];
}
