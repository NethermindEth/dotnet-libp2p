// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Hash;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Core.Tests;

public class PeerIdTests
{
    [TestCaseSource(nameof(PeerIdsWithExpectedMultihashes))]
    public void Test_KeyEncoding(string peerIdString, string cidV1PeerId, string cidV0PeerId, HashType hashType)
    {
        PeerId peerId = new(peerIdString);

        Assert.Multiple(() =>
        {
            Assert.That(peerId.ToCidString(), Is.EqualTo(cidV1PeerId));
            Assert.That(peerId.ToString(), Is.EqualTo(cidV0PeerId));
            Assert.That(Multihash.Decode(peerId.Bytes).Code, Is.EqualTo(hashType));
        });
    }

    [Test]
    public void Test_ExtractPublicKey_ReturnsTheInlinedEd25519Key()
    {
        Identity identity = new();

        Assert.That(PeerId.ExtractPublicKey(identity.PeerId.Bytes), Is.EqualTo(identity.PublicKey));
    }

    /// <remarks>
    /// The padded key verifies its owner's signatures, so a peer ID that inlines
    /// it would give the same keypair a second identity.
    /// </remarks>
    [Test]
    public void Test_ExtractPublicKey_ReturnsNull_WhenInlinedEd25519KeyIsPadded()
    {
        PublicKey key = new Identity().PublicKey;
        PublicKey padded = new() { Type = KeyType.Ed25519, Data = ByteString.CopyFrom([.. key.Data.ToByteArray(), 0x00]) };
        PeerId peerId = new(padded);
        Assert.That(Multihash.Decode(peerId.Bytes).Code, Is.EqualTo(HashType.ID), "precondition: the padded key is still inlined");

        Assert.That(PeerId.ExtractPublicKey(peerId.Bytes), Is.Null);
    }

    [Test]
    public void Test_ExtractPublicKey_ReturnsNull_WhenInlinedKeyIsNotProtobuf()
    {
        byte[] peerId = Multihash.Encode([0xff, 0xff, 0xff], HashType.ID);

        Assert.That(PeerId.ExtractPublicKey(peerId), Is.Null);
    }

    public static IEnumerable<TestCaseData> PeerIdsWithExpectedMultihashes()
    {
        yield return new TestCaseData(
            "bafzbeie5745rpv2m6tjyuugywy4d5ewrqgqqhfnf445he3omzpjbx5xqxe",
            "bafzbeie5745rpv2m6tjyuugywy4d5ewrqgqqhfnf445he3omzpjbx5xqxe",
            "QmYyQSo1c1Ym7orWxLYvCrM2EmxFTANf8wXmmE7DWjhx5N",
            HashType.SHA2_256
            )
        { TestName = "CIDv1, sha256" };

        yield return new TestCaseData(
            "QmYyQSo1c1Ym7orWxLYvCrM2EmxFTANf8wXmmE7DWjhx5N",
            "bafzbeie5745rpv2m6tjyuugywy4d5ewrqgqqhfnf445he3omzpjbx5xqxe",
            "QmYyQSo1c1Ym7orWxLYvCrM2EmxFTANf8wXmmE7DWjhx5N",
            HashType.SHA2_256
            )
        { TestName = "CIDv0, sha256" };

        yield return new TestCaseData(
            "12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA",
            "bafzaajaiaejcal72gwuz2or47oyxxn6b3rkwdmmkrxgkjxzy3rqt5kczyn7lcm3l",
            "12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA",
            HashType.ID
            )
        { TestName = "CIDv0, identity" };
    }
}
