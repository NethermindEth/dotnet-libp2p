// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Runtime.ConstrainedExecution;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Nethermind.Libp2p.Protocols.Quic;

public class CertificateHelper
{
    private const string PubkeyExtensionOidString = "1.3.6.1.4.1.53594.1.1";
    private static readonly Oid PubkeyExtensionOid = new(PubkeyExtensionOidString);
<<<<<<< HEAD
    static X509Certificate2 res = null;
    public static X509Certificate2 CertificateFromIdentity(ECDsa sessionKey, Identity identity)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13;
        byte[] signature = identity.Sign(ContentToSignFromTlsPublicKey(sessionKey.ExportSubjectPublicKeyInfo()));
=======

    public static X509Certificate2 CertificateFromIdentity(ECDsa sessionKey, Identity identity)
    {
        Span<byte> signature = identity.Sign(ContentToSignFromTlsPublicKey(sessionKey.ExportSubjectPublicKeyInfo()));
        AsnWriter asnWriter = new(AsnEncodingRules.DER);
        asnWriter.PushSequence();
        asnWriter.WriteOctetString(identity.PublicKey.ToByteArray());
        asnWriter.WriteOctetString(signature);
        asnWriter.PopSequence();
>>>>>>> upstream/main

        Span<byte> pubkeyExtension = stackalloc byte[asnWriter.GetEncodedLength()];
        int d = asnWriter.Encode(pubkeyExtension);

<<<<<<< HEAD
        CertificateRequest certRequest = new($"cn={new Random().Next()}", sessionKey, HashAlgorithmName.SHA256);

        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, true));
        var result = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        var result0 = result;
        //result = new X509Certificate2(result.Export(X509ContentType.Pfx));
        var result2 = result;

        X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.MaxAllowed);
        var d = store.Certificates.Count;

        store.Add(result);

        var d2 = store.Certificates.Count;

        store.Close();
        store.Dispose();

        try
        {
            store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            X509Certificate2Collection cers = store.Certificates;
            if (cers.Count > 0)
            {
                foreach (X509Certificate2 c in cers)
                {
                    if (c.SerialNumber == result.SerialNumber)
                    {
                        result = c;
                    }
                }
            }
            store.Close();

        }
        catch
        {
            throw;
        }
        res = result;

        return result;// new X509Certificate2(result.Export(X509ContentType.Pkcs7));
    }


    public static (X509Certificate2, X509Certificate2) CertificateFromIdentity2(ECDsa sessionKey, Identity identity)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13;
        byte[] signature = identity.Sign(ContentToSignFromTlsPublicKey(sessionKey.ExportSubjectPublicKeyInfo()));

        AsnWriter asnWrtier = new(AsnEncodingRules.DER);
        asnWrtier.PushSequence();
        asnWrtier.WriteOctetString(identity.PublicKey.ToByteArray());
        asnWrtier.WriteOctetString(signature);
        asnWrtier.PopSequence();
        byte[] pubkeyExtension = new byte[asnWrtier.GetEncodedLength()];
        asnWrtier.Encode(pubkeyExtension);

        CertificateRequest certRequest = new($"cn={new Random().Next()}", sessionKey, HashAlgorithmName.SHA256);

        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, true));
        var result = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        var result0 = result;
        result = new X509Certificate2(result.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.PersistKeySet);
        var result2 = result;

        X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.MaxAllowed);
        var d = store.Certificates.Count;

        store.Add(result);

        var d2 = store.Certificates.Count;
        store.Close();
        store.Dispose();

        try
        {
            store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            X509Certificate2Collection cers = store.Certificates;
            if (cers.Count > 0)
            {
                foreach (X509Certificate2 c in cers)
                {
                    if (c.SerialNumber == result.SerialNumber)
                    {
                        result = c;
                    }
                }
            }

            store.Close();

        }
        catch
        {
            throw;
        }
        res = result;


        return (result, result0);// new X509Certificate2(result.Export(X509ContentType.Pkcs7));
=======
        Span<byte> bytes = stackalloc byte[20];
        Random random = new();
        random.NextBytes(bytes);

        CertificateRequest certRequest = new($"SERIALNUMBER={Convert.ToHexString(bytes)}", sessionKey, HashAlgorithmName.SHA256);

        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, false));

        X509Certificate2 certificate = certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.MaxValue);

        return certificate;
>>>>>>> upstream/main
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
