// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.Quic;
public class CertificateHelper
{
    private const string PubkeyExtensionOidString = "1.3.6.1.4.1.53594.1.1";
    private static readonly Oid PubkeyExtensionOid = new(PubkeyExtensionOidString);

    public static X509Certificate CertificateFromIdentity(ECDsa sessionKey, Identity identity)
    {
        byte[] signature = identity.Sign(ContentToSignFromTlsPublicKey(sessionKey.ExportSubjectPublicKeyInfo()));

        AsnWriter asnWrtier = new(AsnEncodingRules.DER);
        asnWrtier.PushSequence();
        asnWrtier.WriteOctetString(identity.PublicKey.ToByteArray());
        asnWrtier.WriteOctetString(signature);
        asnWrtier.PopSequence();
        byte[] pubkeyExtension = new byte[asnWrtier.GetEncodedLength()];
        asnWrtier.Encode(pubkeyExtension);

        CertificateRequest certRequest = new("", sessionKey, HashAlgorithmName.SHA256);
        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, true));

        return certRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
    }

    public static bool ValidateCertificate(X509Certificate2? certificate, string? peerId)
    {
        if (certificate is null)
        {
            return false;
        }

        X509Extension[] exts = certificate.Extensions.Where(e => e.Oid?.Value == PubkeyExtensionOidString).ToArray();

        if (exts.Length is 0)
        {
            return false;
        }

        if (exts.Length is not 1)
        {
            return false;
        }

        X509Extension ext = exts.First();

        AsnReader a = new(ext.RawData, AsnEncodingRules.DER);
        AsnReader signedKey = a.ReadSequence();

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

    private static readonly byte[] SignaturePrefix = "libp2p-tls-handshake:"u8.ToArray();
    private static byte[] ContentToSignFromTlsPublicKey(byte[] keyInfo) => SignaturePrefix.Concat(keyInfo).ToArray();
}
