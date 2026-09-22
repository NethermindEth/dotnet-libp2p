// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

// cspell:ignore Rsassa

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using AlgorithmIdentifier = Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

public class RsaCertificateTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ValidateCertificate_RejectsMismatchedSignatureAlgorithms(bool pss)
    {
        var identity = new Identity(new byte[32]);
        using RSA key = RSA.Create(2048);
        var padding = pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
        var request = CreateRequest(identity, key, HashAlgorithmName.SHA256, padding);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var sequence = Asn1Sequence.GetInstance(certificate.RawData);
        var body = Asn1Sequence.GetInstance(sequence[0]).ToArray();
        var outerAlgorithm = AlgorithmIdentifier.GetInstance(sequence[1]);
        if (pss)
        {
            var parameters = RsassaPssParameters.GetInstance(outerAlgorithm.Parameters);
            body[2] = new AlgorithmIdentifier(outerAlgorithm.Algorithm, new RsassaPssParameters(
                parameters.HashAlgorithm, parameters.MaskGenAlgorithm,
                DerInteger.ValueOf(parameters.SaltLength.IntValueExact + 1), parameters.TrailerField));
        }
        else
        {
            body[2] = new AlgorithmIdentifier(PkcsObjectIdentifiers.Sha384WithRsaEncryption, DerNull.Instance);
        }

        var modifiedBody = new DerSequence(body);
        byte[] signature = key.SignData(modifiedBody.GetDerEncoded(), HashAlgorithmName.SHA256, padding);
        using var malformed = X509CertificateLoader.LoadCertificate(
            new DerSequence(modifiedBody, outerAlgorithm, new DerBitString(signature)).GetDerEncoded());

        Assert.That(CertificateHelper.ValidateCertificate(malformed, identity.PeerId.ToString()), Is.False);
    }

    [TestCase(true, "SHA256")]
    [TestCase(true, "SHA384")]
    [TestCase(true, "SHA512")]
    [TestCase(false, "SHA256")]
    public void ValidateCertificate_VerifiesRsaSelfSignature(bool pss, string hash)
    {
        var identity = new Identity(new byte[32]);
        using RSA key = RSA.Create(2048);
        var request = CreateRequest(identity, key, new HashAlgorithmName(hash),
            pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.That(CertificateHelper.ValidateCertificate(certificate, identity.PeerId.ToString()), Is.True);

        byte[] tampered = certificate.RawData;
        tampered[^1] ^= 1;
        using var invalid = X509CertificateLoader.LoadCertificate(tampered);
        Assert.That(CertificateHelper.ValidateCertificate(invalid, identity.PeerId.ToString()), Is.False);
    }

    [TestCase(0, false, false, false, true)]
    [TestCase(20, true, false, false, true)]
    [TestCase(20, true, true, false, false)]
    [TestCase(20, true, false, true, false)]
    public void ValidateCertificate_UsesEncodedPssParameters(int saltLength, bool differentMgfHash,
        bool wrongSalt, bool wrongMgfHash, bool expected)
    {
        var identity = new Identity(new byte[32]);
        using RSA key = RSA.Create(2048);
        var request = CreateRequest(identity, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var generator = new PssSignatureGenerator(key, saltLength, differentMgfHash, wrongSalt, wrongMgfHash);
        using var certificate = request.Create(request.SubjectName, generator,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), [1]);

        Assert.That(CertificateHelper.ValidateCertificate(certificate, identity.PeerId.ToString()), Is.EqualTo(expected));
    }

    [Test]
    public void ValidateCertificate_PreservesSerializedCertificateResults()
    {
        // These fixed interoperability vectors do not require Windows named-key creation.
        foreach (var test in CertificateTests.CertificatesSerialized().Take(6))
        {
            using var certificate = X509CertificateLoader.LoadCertificate((byte[])test.Arguments[0]!);
            Assert.That(CertificateHelper.ValidateCertificate(certificate, (string)test.Arguments[1]!),
                Is.EqualTo(test.ExpectedResult), test.TestName);
        }
    }

    private static CertificateRequest CreateRequest(Identity identity, RSA key, HashAlgorithmName hash,
        RSASignaturePadding padding)
    {
        var request = new CertificateRequest("CN=libp2p", key, hash, padding);
        var extension = new AsnWriter(AsnEncodingRules.DER);
        extension.PushSequence();
        extension.WriteOctetString(identity.PublicKey.ToByteArray());
        extension.WriteOctetString(identity.Sign([.. "libp2p-tls-handshake:"u8, .. key.ExportSubjectPublicKeyInfo()]));
        extension.PopSequence();
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.53594.1.1", extension.Encode(), false));
        return request;
    }

    private sealed class PssSignatureGenerator(RSA key, int saltLength, bool differentMgfHash,
        bool wrongSalt, bool wrongMgfHash) : X509SignatureGenerator
    {
        protected override PublicKey BuildPublicKey() => CreateForRSA(key, RSASignaturePadding.Pss).PublicKey;

        public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm)
        {
            var hash = new AlgorithmIdentifier(NistObjectIdentifiers.IdSha256, DerNull.Instance);
            var mgfHash = new AlgorithmIdentifier(differentMgfHash && !wrongMgfHash
                ? NistObjectIdentifiers.IdSha384 : NistObjectIdentifiers.IdSha256, DerNull.Instance);
            var parameters = new RsassaPssParameters(hash,
                new AlgorithmIdentifier(PkcsObjectIdentifiers.IdMgf1, mgfHash),
                DerInteger.ValueOf(wrongSalt ? saltLength + 1 : saltLength), DerInteger.One);
            return new AlgorithmIdentifier(PkcsObjectIdentifiers.IdRsassaPss, parameters).GetDerEncoded();
        }

        public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm)
        {
            var signer = new PssSigner(new RsaEngine(), DigestUtilities.GetDigest("SHA256"),
                DigestUtilities.GetDigest(differentMgfHash ? "SHA384" : "SHA256"), saltLength);
            signer.Init(true, PrivateKeyFactory.CreateKey(key.ExportPkcs8PrivateKey()));
            signer.BlockUpdate(data, 0, data.Length);
            return signer.GenerateSignature();
        }
    }
}
