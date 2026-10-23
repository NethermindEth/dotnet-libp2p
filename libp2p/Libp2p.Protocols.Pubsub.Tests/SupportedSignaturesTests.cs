// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Org.BouncyCastle.Crypto.EC;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

public class SupportedSignaturesTests
{
    public static IEnumerable<TestCaseData> Vectors => IdentityFixtures.Signatures.Select(v => new TestCaseData(v).SetName($"IndependentPubsubSignature_{v.Type}"));

    [TestCaseSource(nameof(Vectors))]
    public void IndependentPubsubSignature(IdentityFixtures.Vector vector)
    {
        Message msg = Message.Parser.ParseFrom(Convert.FromHexString(vector.MessageHex));
        msg.Signature = ByteString.CopyFrom(Convert.FromHexString(vector.SignatureHex));
        msg.Key = ByteString.CopyFrom(Convert.FromHexString(vector.PublicKeyHex));
        byte[] original = msg.ToByteArray();
        Assert.That(msg.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.True);
        Assert.That(msg.ToByteArray(), Is.EqualTo(original));
        msg.ClearKey();
        Assert.That(msg.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.EqualTo(vector.Type is KeyType.Ed25519 or KeyType.Secp256K1));
        msg.Key = ByteString.CopyFrom(Convert.FromHexString(vector.PublicKeyHex));
        msg.Data = ByteString.CopyFromUtf8("tampered");
        Assert.That(msg.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False);
    }

    [TestCase(KeyType.Ed25519)]
    [TestCase(KeyType.Secp256K1)]
    [TestCase(KeyType.Rsa)]
    [TestCase(KeyType.Ecdsa)]
    public void PublisherAttachesOnlyRequiredKeysAndRouterDoesNotPenalizeValidMessages(KeyType type)
    {
        Identity author = new(keyType: type);
        using PubsubRouter router = new(new PeerStore());
        List<byte[]> deliveries = [];
        router.GetTopic("signed").OnMessage += (_, data) => deliveries.Add(data);
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, new TaskCompletionSource().Task, _ => { });
        List<Rpc> forwarded = [];
        router.OutboundConnection(TestPeers.Multiaddr(2), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task, forwarded.Add);
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["signed"], []));
        forwarded.Clear();
        for (ulong i = 1; i <= 20; i++)
        {
            Rpc rpc = new Rpc().WithMessages("signed", i, author.PeerId.Bytes, [(byte)i], author);
            Assert.That(rpc.Publish[0].HasKey, Is.EqualTo(type is KeyType.Rsa or KeyType.Ecdsa));
            router.OnRpc(TestPeers.PeerId(1), rpc);
        }
        Assert.That(deliveries, Has.Count.EqualTo(20));
        Assert.That(forwarded.SelectMany(r => r.Publish).Count(), Is.EqualTo(20));
        Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.GreaterThanOrEqualTo(0));

        Rpc invalid = new Rpc().WithMessages("signed", 21, author.PeerId.Bytes, [21], author);
        invalid.Publish[0].Signature = ByteString.CopyFrom([1]);
        invalid.WithMessages("signed", 23, author.PeerId.Bytes, [23], author);
        invalid.Publish[1].Signature = ByteString.CopyFrom([1]);
        invalid.WithMessages("signed", 22, author.PeerId.Bytes, [22], author);
        double priorScore = router.GetPeerScore(TestPeers.PeerId(1));
        router.OnRpc(TestPeers.PeerId(1), invalid);
        Assert.That(deliveries, Has.Count.EqualTo(21));
        Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.LessThan(priorScore));
    }

    [TestCase("empty")]
    [TestCase("malformed")]
    [TestCase("other")]
    public void ExplicitInvalidKeyCannotFallBackToInlineKey(string shape)
    {
        Identity author = new();
        Message msg = new Rpc().WithMessages("signed", 1, author.PeerId.Bytes, [1], author).Publish[0];
        msg.Key = shape switch
        {
            "empty" => ByteString.Empty,
            "malformed" => ByteString.CopyFrom([0xff]),
            _ => new Identity().PublicKey.ToByteString(),
        };
        Assert.That(msg.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False);
    }

    [Test]
    public void ExplicitSecp256k1KeyIsCanonicalizedBeforeBinding()
    {
        Identity author = new(keyType: KeyType.Secp256K1);
        Message msg = new Rpc().WithMessages("signed", 1, author.PeerId.Bytes, [1], author).Publish[0];
        byte[] point = CustomNamedCurves.GetByName("secp256k1").Curve.DecodePoint(author.PublicKey.Data.ToByteArray()).GetEncoded(false);
        PublicKey key = new() { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(point) };
        msg.Key = ByteString.CopyFrom([.. key.ToByteArray(), 0x18, 0x01]);
        Assert.That(msg.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.True);
    }

    [Test]
    public void ExplicitKeyCannotSignForAnotherSender()
    {
        Identity attacker = new();
        Identity victim = new();
        Message msg = new Rpc().WithMessages("signed", 1, victim.PeerId.Bytes, [1], attacker).Publish[0];
        msg.Key = attacker.PublicKey.ToByteString();
        Assert.That(msg.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False,
            "Even a valid signature by the explicit key cannot authenticate a different sender.");
    }
}
