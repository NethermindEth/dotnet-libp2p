// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Org.BouncyCastle.Crypto.EC;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class SigningHelperTests
{
    /// <remarks>
    /// Identify only catches protobuf errors around this call, so a key that does
    /// not decode must come back as <c>false</c> rather than as an exception.
    /// </remarks>
    [TestCaseSource(nameof(UndecodableKeys))]
    public void Test_VerifyPeerRecord_ReturnsFalse_WhenEnvelopeKeyDoesNotDecode(ByteString key)
    {
        Identity peer = new();
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SigningHelper.CreateSignedEnvelope(peer, [], 1));
        envelope.PublicKey = key;

        Assert.That(SigningHelper.VerifyPeerRecord(envelope, peer.PublicKey, out _), Is.False);
    }

    public static IEnumerable<TestCaseData> UndecodableKeys()
    {
        PublicKey ed25519 = new Identity().PublicKey;
        yield return new TestCaseData(ByteString.CopyFrom([0xff, 0xff, 0xff])).SetName("Test_VerifyPeerRecord_ReturnsFalse_WhenEnvelopeKeyIsNotProtobuf");
        yield return new TestCaseData(Key((KeyType)7, [1, 2, 3]).ToByteString()).SetName("Test_VerifyPeerRecord_ReturnsFalse_WhenEnvelopeKeyIsOfUnknownType");
        yield return new TestCaseData(Key(KeyType.Ed25519, [.. ed25519.Data.ToByteArray(), 0x00]).ToByteString()).SetName("Test_VerifyPeerRecord_ReturnsFalse_WhenEnvelopeEd25519KeyIsPadded");
    }

    /// <remarks>
    /// The session key is compressed, as every handshake now stores it, while the
    /// envelope carries the same point uncompressed. go-libp2p derives the same
    /// peer ID from either encoding.
    /// </remarks>
    [TestCase(false, TestName = "Test_VerifyPeerRecord_ReturnsTrue_WhenEnvelopeCarriesTheSessionKeyCompressed")]
    [TestCase(true, TestName = "Test_VerifyPeerRecord_ReturnsTrue_WhenEnvelopeCarriesTheSessionKeyUncompressed")]
    public void Test_VerifyPeerRecord_ReturnsTrue_ForEitherSecp256k1Encoding(bool uncompressed)
    {
        Identity peer = new(keyType: KeyType.Secp256K1);
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SigningHelper.CreateSignedEnvelope(peer, [], 7));
        if (uncompressed)
        {
            byte[] point = CustomNamedCurves.GetByName("secp256k1").Curve.DecodePoint(peer.PublicKey.Data.ToByteArray()).GetEncoded(false);
            envelope.PublicKey = Key(KeyType.Secp256K1, point).ToByteString();
        }

        Assert.Multiple(() =>
        {
            Assert.That(SigningHelper.VerifyPeerRecord(envelope, peer.PublicKey, out ulong seq), Is.True);
            Assert.That(seq, Is.EqualTo(7));
        });
    }

    private static PublicKey Key(KeyType keyType, byte[] data) =>
        new() { Type = keyType, Data = ByteString.CopyFrom(data) };
}
