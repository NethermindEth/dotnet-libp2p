// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using NUnit.Framework;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class PublicKeyDecoderTests
{
    [Test]
    public void Test_GeneratedKeyRoundTrips()
    {
        PublicKey generated = new Identity().PublicKey;

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
}
