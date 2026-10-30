// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Hash;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Org.BouncyCastle.Crypto.EC;
using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Core.Tests;

public class CanonicalIdentityTests
{
    public static IEnumerable<TestCaseData> Vectors => IdentityFixtures.Signatures.Select(v => new TestCaseData(v).SetName($"CanonicalIdentity_{v.Type}"));

    [TestCaseSource(nameof(Vectors))]
    public void CanonicalIdentity(IdentityFixtures.Vector vector)
    {
        PublicKey key = PublicKeyDecoder.Decode(ByteString.CopyFrom(Convert.FromHexString(vector.PublicKeyHex)));
        PeerId id = new(key);
        Assert.That(id.ToString(), Is.EqualTo(vector.PeerId));
        bool inline = vector.Type is KeyType.Ed25519 or KeyType.Secp256K1;
        Assert.That(Multihash.Decode(id.Bytes).Code, Is.EqualTo(inline ? HashType.ID : HashType.SHA2_256));
        Assert.That(PeerId.ExtractPublicKey(id.Bytes), Is.EqualTo(inline ? key : null));

        PublicKey extended = PublicKey.Parser.ParseFrom([.. key.ToByteArray(), 0x18, 0x01]);
        byte[] original = extended.ToByteArray();
        Assert.That(PublicKeyDecoder.Decode(extended.ToByteString()), Is.EqualTo(key));
        Assert.That(new PeerId(extended), Is.EqualTo(id));
        Assert.That(extended.ToByteArray(), Is.EqualTo(original), "Construction must not mutate caller-owned keys.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExplicitEcdsaParametersCannotCreateAnAlternatePeerId(bool constructPeerId)
    {
        IdentityFixtures.Vector vector = IdentityFixtures.Signatures.Single(v => v.Type == KeyType.Ecdsa);
        PublicKey named = PublicKey.Parser.ParseFrom(Convert.FromHexString(vector.PublicKeyHex));
        PublicKey explicitKey = new() { Type = KeyType.Ecdsa, Data = ByteString.CopyFrom(IdentityFixtures.ExplicitP256PublicKey) };
        byte[] payload = [.. "libp2p-pubsub:"u8, .. Convert.FromHexString(vector.MessageHex)];
        byte[] signature = Convert.FromHexString(vector.SignatureHex);
        Assert.That(new Identity(named).VerifySignature(payload, signature), Is.True);
        Assert.That(new Identity(explicitKey).VerifySignature(payload, signature), Is.True, "The alternate encoding authenticates the same private key.");
        Assert.That(new PeerId((byte[])Multihash.Sum(HashType.SHA2_256, explicitKey.ToByteArray())), Is.Not.EqualTo(new PeerId(named)));
        Assert.That(new Identity(IdentityFixtures.P256PrivateKey, KeyType.Ecdsa).PublicKey, Is.EqualTo(named));

        if (constructPeerId) Assert.Throws<Libp2pException>(() => _ = new PeerId(explicitKey));
        else Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(explicitKey.ToByteString()));
        Assert.That(PublicKeyDecoder.Decode(named.ToByteString()), Is.EqualTo(named));
    }

    [Test]
    public void RsaSubjectPublicKeyInfoNormalizationRestoresNullParameters()
    {
        IdentityFixtures.Vector vector = IdentityFixtures.Signatures.Single(v => v.Type == KeyType.Rsa);
        PublicKey canonical = PublicKey.Parser.ParseFrom(Convert.FromHexString(vector.PublicKeyHex));
        AsnReader publicKeyInfo = new AsnReader(canonical.Data.Memory, AsnEncodingRules.DER).ReadSequence();
        AsnReader algorithm = publicKeyInfo.ReadSequence();
        string algorithmOid = algorithm.ReadObjectIdentifier();
        algorithm.ReadNull();
        byte[] keyBits = publicKeyInfo.ReadBitString(out int unusedBits);
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence()) writer.WriteObjectIdentifier(algorithmOid);
            writer.WriteBitString(keyBits, unusedBits);
        }
        // The importer accepts omitted RSA parameters; the canonical export includes NULL.
        byte[] withoutParameters = writer.Encode();
        PublicKey alternate = new() { Type = KeyType.Rsa, Data = ByteString.CopyFrom(withoutParameters) };

        Assert.That(PublicKeyDecoder.Decode(alternate.ToByteString()), Is.EqualTo(canonical));
        Assert.That(new PeerId(alternate).ToString(), Is.EqualTo(vector.PeerId));
        Assert.That(alternate.Data.ToByteArray(), Is.EqualTo(withoutParameters));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Secp256k1ConstructionCompressesWithoutChangingTheInput(bool hybrid)
    {
        Identity key = new([1], KeyType.Secp256K1);
        byte[] point = CustomNamedCurves.GetByName("secp256k1").Curve.DecodePoint(key.PublicKey.Data.ToByteArray()).GetEncoded(false);
        if (hybrid) point[0] = 0x06;
        PublicKey alternate = new() { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(point) };
        Assert.That(new PeerId(alternate), Is.EqualTo(key.PeerId));
        Assert.That(alternate.Data.ToByteArray(), Is.EqualTo(point));
        Assert.That(PeerId.ExtractPublicKey(Multihash.Encode(alternate.ToByteArray(), HashType.ID)), Is.Null, "Oversized inline aliases are invalid.");
    }

    [Test]
    public void ExtractionRejectsNonCanonicalIdentityMultihashes()
    {
        byte[] key = new Identity().PublicKey.ToByteArray();
        byte[] canonical = Multihash.Encode(key, HashType.ID);
        byte[][] aliases =
        [
            Multihash.Encode([.. key, 0x18, 0x01], HashType.ID),
            Multihash.Encode([0x12, 0x20, .. key[4..], 0x08, 0x01], HashType.ID),
            Multihash.Encode([0x08, 0x81, 0x00, .. key[2..]], HashType.ID),
            [.. canonical, 0x00],
        ];
        foreach (byte[] alias in aliases)
        {
            Assert.That(PeerId.ExtractPublicKey(alias), Is.Null);
        }
    }

    [TestCase(512)]
    [TestCase(1024)]
    public void RemoteRsaRequires2048BitsButLegacyImportsRemainReadable(int bits)
    {
        Identity legacy = new(IdentityFixtures.LegacyRsaPrivateKey, KeyType.Rsa);
        PublicKey key = bits == 1024 ? legacy.PublicKey : new PublicKey { Type = KeyType.Rsa, Data = ByteString.CopyFrom(IdentityFixtures.Rsa512PublicKey) };
        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(key.ToByteString()));
        Assert.That(legacy.PrivateKey!.Data.ToByteArray(), Is.EqualTo(IdentityFixtures.LegacyRsaPrivateKey));
        Assert.That(legacy.VerifySignature([1, 2, 3], legacy.Sign([1, 2, 3])), Is.True);
        Assert.DoesNotThrow(() => _ = legacy.PeerId);
    }

    [Test]
    public void GeneratedRsaUses2048Bits()
    {
        Identity identity = new(keyType: KeyType.Rsa);
        using RSA rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(identity.PublicKey.Data.Span, out _);
        Assert.That(rsa.KeySize, Is.EqualTo(2048));
        Assert.That(PublicKeyDecoder.Decode(identity.PublicKey.ToByteString()), Is.EqualTo(identity.PublicKey));
    }

    [Test]
    public void RsaMinimumCountsSignificantModulusBits()
    {
        using RSA rsa = RSA.Create();
        IdentityFixtures.Vector fixture = IdentityFixtures.Signatures.Single(v => v.Type == KeyType.Rsa);
        PublicKey key = PublicKey.Parser.ParseFrom(Convert.FromHexString(fixture.PublicKeyHex));
        rsa.ImportSubjectPublicKeyInfo(key.Data.Span, out _);
        RSAParameters parameters = rsa.ExportParameters(false);
        parameters.Modulus![0] = 0x40;
        rsa.ImportParameters(parameters);
        key.Data = ByteString.CopyFrom(rsa.ExportSubjectPublicKeyInfo());
        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(key.ToByteString()));
    }
}
