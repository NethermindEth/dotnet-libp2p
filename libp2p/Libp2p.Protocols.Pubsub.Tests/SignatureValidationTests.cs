// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class SignatureValidationTests
{
    [Test]
    public void MalformedPeerId_DoesNotDropFollowingValidMessage()
        => AssertBatchContinuesAfterMalformedMessage(message => message.From = ByteString.CopyFrom([1]));

    [Test]
    public void ShortSignature_DoesNotDropFollowingValidMessage()
        => AssertBatchContinuesAfterMalformedMessage(message => message.Signature = ByteString.CopyFrom(new byte[63]));

    [Test]
    public void ShortEmbeddedPublicKey_DoesNotDropFollowingValidMessage()
    {
        var shortKey = new Identity().PublicKey.Clone();
        shortKey.Data = ByteString.CopyFrom(new byte[31]);
        AssertBatchContinuesAfterMalformedMessage(message => message.From = ByteString.CopyFrom(new PeerId(Multiformats.Hash.Multihash.Encode(shortKey.ToByteArray(), Multiformats.Hash.HashType.ID)).Bytes));
    }

    [Test]
    public void SignatureCoversPrefixAndSerializedMessage()
    {
        Identity author = TestPeers.Identity(1);
        Message message = new Rpc().WithMessages("signed", 7, author.PeerId.Bytes, [1, 2, 3], author).Publish[0];
        Message unsigned = message.Clone();
        unsigned.ClearSignature();
        byte[] expected = [.. "libp2p-pubsub:"u8, .. unsigned.ToByteArray()];

        Assert.That(message.Signature.ToByteArray(), Is.EqualTo(author.Sign(expected)));
        Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.True);
    }

    private static void AssertBatchContinuesAfterMalformedMessage(Action<Message> makeMalformed)
    {
        using PubsubRouter router = new(new PeerStore());
        List<byte[]> deliveries = [];
        router.GetTopic("signed").OnMessage += (_, data) => deliveries.Add(data);
        Identity author = new();
        Rpc rpc = new();
        rpc.WithMessages("signed", 1, author.PeerId.Bytes, [1], author);
        makeMalformed(rpc.Publish[0]);
        rpc.WithMessages("signed", 2, author.PeerId.Bytes, [2], author);

        router.OnRpc(TestPeers.PeerId(1), rpc);

        Assert.That(deliveries, Is.EqualTo(new[] { new byte[] { 2 } }));
    }
}
