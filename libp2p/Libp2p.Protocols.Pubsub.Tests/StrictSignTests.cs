// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class StrictSignTests
{
    [Test]
    public void StrictSign_AcceptsAMessageSignedByItsAuthor()
    {
        Identity author = new();

        Assert.That(Signed(author, author.PeerId).VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.True);
    }

    /// <remarks>
    /// The padded key verifies the author's signature, so the message would pass as
    /// coming from a second peer ID. The length check that stopped it now lives in
    /// the key decoder rather than here.
    /// </remarks>
    [Test]
    public void StrictSign_RejectsAMessageWhosePeerIdCarriesAPaddedKey()
    {
        Identity author = new();
        PublicKey padded = new() { Type = KeyType.Ed25519, Data = ByteString.CopyFrom([.. author.PublicKey.Data.ToByteArray(), 0x00]) };

        Assert.That(Signed(author, new PeerId(Multiformats.Hash.Multihash.Encode(padded.ToByteArray(), Multiformats.Hash.HashType.ID))).VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False);
    }

    private static Message Signed(Identity author, PeerId from) =>
        new Rpc().WithMessages("strict-sign", 1, from.Bytes, [1, 2, 3], author).Publish[0];
}
