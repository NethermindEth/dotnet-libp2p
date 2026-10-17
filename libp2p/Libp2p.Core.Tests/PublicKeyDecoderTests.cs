// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using NUnit.Framework;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Utilities;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class PublicKeyDecoderTests
{
    /// <summary>
    /// js-libp2p's go-generated secp256k1 fixture (y even) and its negation (y odd),
    /// with the peer IDs go-libp2p v0.50.0 derives for them from every encoding.
    /// </summary>
    private const string EvenYKey = "029c0ce5d53646ed47112560297a3e59b78b8cbd4bae37c7a0c236eeb91d0fbeaf";
    private const string EvenYPeerId = "16Uiu2HAm5vpzEwJ41kQmnwDu9moFusdc16wV1oCUd1AHLgFgPpKY";
    private const string OddYKey = "039c0ce5d53646ed47112560297a3e59b78b8cbd4bae37c7a0c236eeb91d0fbeaf";
    private const string OddYPeerId = "16Uiu2HAmPA9Mjj3eoo9YN21KNwyZ76S8Fbaom1Ga747cCbgSumZp";

    private static readonly X9ECParameters Secp256k1 = CustomNamedCurves.GetByName("secp256k1");

    [TestCase(KeyType.Ed25519)]
    [TestCase(KeyType.Secp256K1)]
    [TestCase(KeyType.Rsa)]
    [TestCase(KeyType.Ecdsa)]
    public void Test_GeneratedKeyRoundTrips(KeyType keyType)
    {
        PublicKey generated = new Identity(keyType: keyType).PublicKey;

        PublicKey decoded = PublicKeyDecoder.Decode(generated.ToByteString());

        Assert.That(decoded, Is.EqualTo(generated));
    }

    [Test]
    public void Test_Decode_Fails_WhenBytesAreNotProtobuf()
    {
        Assert.Throws<Libp2pException>(
            () => PublicKeyDecoder.Decode(ByteString.CopyFrom([0xff, 0xff, 0xff, 0xff])));
    }

    [Test]
    public void Test_Decode_Fails_WhenTypeIsMissing()
    {
        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(ByteString.Empty));
    }

    [Test]
    public void Test_Decode_Fails_WhenTypeIsUnknown()
    {
        PublicKey key = new() { Type = (KeyType)7, Data = ByteString.CopyFrom([1, 2, 3, 4]) };

        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(key.ToByteString()));
    }

    /// <remarks>
    /// Ed25519 verification reads the first 32 bytes and ignores the rest, so a
    /// padded key verifies the same signatures under a different peer ID. Only
    /// an explicit length check rejects it.
    /// </remarks>
    [TestCase(0, TestName = "Test_Decode_Fails_WhenEd25519KeyIsEmpty")]
    [TestCase(1, TestName = "Test_Decode_Fails_WhenEd25519KeyIsOneByte")]
    [TestCase(31, TestName = "Test_Decode_Fails_WhenEd25519KeyIsTruncated")]
    [TestCase(33, TestName = "Test_Decode_Fails_WhenEd25519KeyIsPadded")]
    public void Test_Decode_Fails_WhenEd25519KeyLengthIsWrong(int length)
    {
        PublicKey key = new()
        {
            Type = KeyType.Ed25519,
            Data = ByteString.CopyFrom(new byte[length]),
        };

        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(key.ToByteString()));
    }

    [Test]
    public void Test_PaddedEd25519KeyVerifiesTheSameSignature()
    {
        Identity identity = new();
        byte[] message = [1, 2, 3, 4];
        byte[] signature = identity.Sign(message);
        byte[] exact = identity.PublicKey.Data.ToByteArray();
        byte[] padded = [.. exact, (byte)0x00];

        Assert.Multiple(() =>
        {
            Assert.That(
                Ed25519.Verify(signature, 0, padded, 0, message, 0, message.Length),
                Is.True,
                "padded key still verifies, which is why the length check is explicit");
            Assert.That(
                new PeerId(new PublicKey { Type = KeyType.Ed25519, Data = ByteString.CopyFrom(padded) }),
                Is.Not.EqualTo(new PeerId(identity.PublicKey)),
                "and it yields a different peer ID");
        });
    }

    [TestCase(KeyType.Rsa, TestName = "Test_Decode_Fails_WhenRsaKeyHasTrailingBytes")]
    [TestCase(KeyType.Ecdsa, TestName = "Test_Decode_Fails_WhenEcdsaKeyHasTrailingBytes")]
    public void Test_Decode_Fails_WhenDerKeyHasTrailingBytes(KeyType keyType)
    {
        PublicKey valid = new Identity(keyType: keyType).PublicKey;
        PublicKey padded = new()
        {
            Type = keyType,
            Data = ByteString.CopyFrom([.. valid.Data.ToByteArray(), (byte)0x00]),
        };

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => PublicKeyDecoder.Decode(valid.ToByteString()));
            Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(padded.ToByteString()));
        });
    }

    [TestCase(KeyType.Secp256K1)]
    [TestCase(KeyType.Rsa)]
    [TestCase(KeyType.Ecdsa)]
    public void Test_Decode_Fails_WhenKeyMaterialDoesNotParse(KeyType keyType)
    {
        PublicKey key = new() { Type = keyType, Data = ByteString.CopyFrom([1, 2, 3, 4]) };

        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(key.ToByteString()));
    }

    /// <remarks>
    /// Checks usability in both directions: a generated key decodes, and anything
    /// the decoder accepts can verify without throwing. That catches unusable
    /// material such as the secp256k1 point at infinity or a truncated key. It
    /// does not catch extra spellings of a valid key — padded Ed25519, DER
    /// trailing bytes — because those verify fine and only the peer ID changes.
    /// The per-shape cases are what hold those, so they are not redundant with
    /// this one.
    /// </remarks>
    [Test]
    public void Test_AcceptedKeysVerifyWithoutThrowing()
    {
        byte[] message = [1, 2, 3];
        byte[] signature = new byte[64];
        List<(string Name, PublicKey Key, bool IsGeneratedKey)> shapes = [];

        foreach (KeyType keyType in Enum.GetValues<KeyType>())
        {
            PublicKey valid = new Identity(keyType: keyType).PublicKey;
            shapes.Add(($"{keyType}", valid, true));
            shapes.Add(($"{keyType} with a trailing byte", Key(keyType, [.. valid.Data.ToByteArray(), 0x00]), false));
            shapes.Add(($"{keyType} with no data", Key(keyType, []), false));
            shapes.Add(($"{keyType} as a single zero byte", Key(keyType, [0x00]), false));
            shapes.Add(($"{keyType} truncated by one byte", Key(keyType, valid.Data.ToByteArray()[..^1]), false));
        }

        Assert.Multiple(() =>
        {
            foreach ((string name, PublicKey key, bool isGeneratedKey) in shapes)
            {
                PublicKey decoded;

                try
                {
                    decoded = PublicKeyDecoder.Decode(key.ToByteString());
                }
                catch (Libp2pException e)
                {
                    Assert.That(isGeneratedKey, Is.False, $"{name} is a usable key but was rejected: {e.Message}");
                    continue;
                }

                Assert.DoesNotThrow(
                    () => new Identity(decoded).VerifySignature(message, signature),
                    $"{name} was accepted by the decoder but cannot be used to verify.");
            }
        });
    }

    /// <remarks>
    /// Verification succeeds for every encoding of a key, so the invariant above
    /// cannot see a regression here; only the peer ID would change. The expected
    /// IDs are go-libp2p's, so this cannot pass by agreeing with itself.
    /// </remarks>
    [TestCaseSource(nameof(Secp256k1Encodings))]
    public void Test_Decode_CompressesSecp256k1Key(byte[] encoded, string compressedHex, string expectedPeerId)
    {
        PublicKey decoded = PublicKeyDecoder.Decode(Key(KeyType.Secp256K1, encoded).ToByteString());

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Data.ToByteArray(), Is.EqualTo(Convert.FromHexString(compressedHex)));
            Assert.That(new PeerId(decoded).ToString(), Is.EqualTo(expectedPeerId));
        });
    }

    /// <remarks>
    /// Compressing to the wrong y would still decode, to the negated point; only
    /// a signature from the original keypair tells the two apart.
    /// </remarks>
    [TestCase(false, TestName = "Test_DecodedSecp256k1Key_VerifiesSignaturesFromThePrivateKey_EvenY")]
    [TestCase(true, TestName = "Test_DecodedSecp256k1Key_VerifiesSignaturesFromThePrivateKey_OddY")]
    public void Test_DecodedSecp256k1Key_VerifiesSignaturesFromThePrivateKey(bool oddY)
    {
        // G has an even y, so private key 1 gives an even y and n - 1 (which is -G) an odd one.
        BigInteger secret = oddY ? Secp256k1.N.Subtract(BigInteger.One) : BigInteger.One;
        Identity signer = new(secret.ToByteArrayUnsigned(), KeyType.Secp256K1);
        Assert.That(signer.PublicKey.Data[0], Is.EqualTo(oddY ? 0x03 : 0x02), "precondition: the keypair has the intended y parity");
        byte[] message = [1, 2, 3];
        byte[] signature = signer.Sign(message);

        Assert.Multiple(() =>
        {
            foreach ((string form, byte[] encoded) in Encodings(Point(signer.PublicKey.Data.ToByteArray())))
            {
                PublicKey decoded = PublicKeyDecoder.Decode(Key(KeyType.Secp256K1, encoded).ToByteString());

                Assert.That(new Identity(decoded).VerifySignature(message, signature), Is.True, form);
            }
        });
    }

    [TestCaseSource(nameof(InvalidSecp256k1Encodings))]
    public void Test_Decode_Fails_WhenSecp256k1EncodingIsInvalid(byte[] encoded)
    {
        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(Key(KeyType.Secp256K1, encoded).ToByteString()));
    }

    private static IEnumerable<TestCaseData> Secp256k1Encodings()
    {
        foreach ((string parity, string compressedHex, string peerId) in new[]
                 {
                     ("EvenY", EvenYKey, EvenYPeerId),
                     ("OddY", OddYKey, OddYPeerId),
                 })
        {
            foreach ((string form, byte[] encoded) in Encodings(Point(Convert.FromHexString(compressedHex))))
            {
                yield return new TestCaseData(encoded, compressedHex, peerId)
                    .SetName($"Test_Decode_CompressesSecp256k1Key_{form}_{parity}");
            }
        }
    }

    /// <summary>
    /// Every class of malformed SEC1 input. go-libp2p rejects each of these too.
    /// </summary>
    private static IEnumerable<TestCaseData> InvalidSecp256k1Encodings()
    {
        ECPoint evenY = Point(Convert.FromHexString(EvenYKey));
        ECPoint oddY = Point(Convert.FromHexString(OddYKey));
        byte[] compressed = evenY.GetEncoded(true);
        byte[] uncompressed = evenY.GetEncoded(false);
        byte[] fieldPrime = FieldElement(Secp256k1.Curve.Field.Characteristic);
        byte[] allOnes = Enumerable.Repeat((byte)0xff, 32).ToArray();
        // x = 1 is the smallest x on the curve; x + p is a second spelling of it.
        ECPoint smallX = Point([0x02, .. FieldElement(BigInteger.One)]);
        byte[] smallXCoord = FieldElement(smallX.AffineXCoord.ToBigInteger());
        byte[] smallYCoord = FieldElement(smallX.AffineYCoord.ToBigInteger());
        byte[] smallXPlusPrime = FieldElement(BigInteger.One.Add(Secp256k1.Curve.Field.Characteristic));

        (string Name, byte[] Encoded)[] cases =
        [
            ("Empty", []),
            ("PointAtInfinity", [0x00]),
            ("InfinityPrefixAtCompressedLength", new byte[33]),
            ("InfinityPrefixAtUncompressedLength", new byte[65]),
            ("CompressedPrefixAlone", [0x02]),
            ("CompressedWithTrailingByte", [.. compressed, 0x00]),
            ("CompressedTruncated", compressed[..^1]),
            ("UncompressedWithTrailingByte", [.. uncompressed, 0x00]),
            ("UncompressedTruncated", uncompressed[..^1]),
            ("UncompressedPrefixAtCompressedLength", WithPrefix(compressed, 0x04)),
            ("HybridPrefixAtCompressedLength", WithPrefix(compressed, 0x06)),
            ("CompressedPrefixAtUncompressedLength", WithPrefix(uncompressed, 0x02)),
            ("UnknownPrefix01", WithPrefix(compressed, 0x01)),
            ("CompactPrefix05", WithPrefix(compressed, 0x05)),
            ("UnknownPrefix08", WithPrefix(uncompressed, 0x08)),
            ("UnknownPrefixFF", WithPrefix(uncompressed, 0xff)),
            ("XOnly", compressed[1..]),
            ("CompressedXWithNoPoint", [0x02, .. new byte[32]]),
            ("CompressedXEqualToFieldPrime", [0x02, .. fieldPrime]),
            ("CompressedXAboveFieldPrime", [0x02, .. allOnes]),
            ("CompressedXPlusFieldPrime", [0x02, .. smallXPlusPrime]),
            ("UncompressedXEqualToFieldPrime", [0x04, .. fieldPrime, .. smallYCoord]),
            ("UncompressedYEqualToFieldPrime", [0x04, .. smallXCoord, .. fieldPrime]),
            ("UncompressedYAboveFieldPrime", [0x04, .. smallXCoord, .. allOnes]),
            ("UncompressedXPlusFieldPrime", [0x04, .. smallXPlusPrime, .. smallYCoord]),
            // y ^ 2 moves the point off the curve without changing y's parity.
            ("UncompressedOffCurve", OffCurve(uncompressed)),
            ("HybridOffCurve", OffCurve(Hybrid(evenY))),
            ("HybridEvenPrefixForOddY", WithPrefix(oddY.GetEncoded(false), 0x06)),
            ("HybridOddPrefixForEvenY", WithPrefix(uncompressed, 0x07)),
        ];

        return cases.Select(c => new TestCaseData(c.Encoded).SetName($"Test_Decode_Fails_WhenSecp256k1KeyIs{c.Name}"));
    }

    private static (string Form, byte[] Encoded)[] Encodings(ECPoint point) =>
    [
        ("Compressed", point.GetEncoded(true)),
        ("Uncompressed", point.GetEncoded(false)),
        ("Hybrid", Hybrid(point)),
    ];

    private static ECPoint Point(byte[] encoded) => Secp256k1.Curve.DecodePoint(encoded).Normalize();

    private static byte[] Hybrid(ECPoint point) =>
        WithPrefix(point.GetEncoded(false), (byte)(point.AffineYCoord.ToBigInteger().TestBit(0) ? 0x07 : 0x06));

    private static byte[] WithPrefix(byte[] encoded, byte prefix) => [prefix, .. encoded[1..]];

    private static byte[] OffCurve(byte[] encoded) => [.. encoded[..^1], (byte)(encoded[^1] ^ 0x02)];

    private static byte[] FieldElement(BigInteger value) => BigIntegers.AsUnsignedByteArray(32, value);

    private static PublicKey Key(KeyType keyType, byte[] data) =>
        new() { Type = keyType, Data = ByteString.CopyFrom(data) };
}
