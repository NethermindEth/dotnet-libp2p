// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;

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

    [Test]
    public void ExplicitEcdsaParametersCannotAuthenticateAnAlias()
    {
        IdentityFixtures.Vector vector = IdentityFixtures.Signatures.Single(v => v.Type == KeyType.Ecdsa);
        Message message = Message.Parser.ParseFrom(Convert.FromHexString(vector.MessageHex));
        message.Signature = ByteString.CopyFrom(Convert.FromHexString(vector.SignatureHex));
        message.Key = ByteString.CopyFrom(Convert.FromHexString(vector.PublicKeyHex));
        Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.True);
        PublicKey explicitKey = new() { Type = KeyType.Ecdsa, Data = ByteString.CopyFrom(IdentityFixtures.ExplicitP256PublicKey) };
        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(explicitKey.Data.Span, out _);
        PublicKey exported = new() { Type = KeyType.Ecdsa, Data = ByteString.CopyFrom(key.ExportSubjectPublicKeyInfo()) };
        PeerId alias = new((byte[])Multiformats.Hash.Multihash.Sum(Multiformats.Hash.HashType.SHA2_256, exported.ToByteArray()));
        Assert.That(alias, Is.Not.EqualTo(new Identity(IdentityFixtures.P256PrivateKey, KeyType.Ecdsa).PeerId));
        message.From = ByteString.CopyFrom(alias.Bytes);
        message.ClearKey();
        message.ClearSignature();
        message.Signature = ByteString.CopyFrom(new Identity(IdentityFixtures.P256PrivateKey, KeyType.Ecdsa).Sign(
            [.. "libp2p-pubsub:"u8, .. message.ToByteArray()]));
        message.Key = explicitKey.ToByteString();
        Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False);
    }

    [Test]
    public async Task LegacyRsaCanPublishWithoutBypassingRemoteAdmission()
    {
        Identity legacy = new(IdentityFixtures.LegacyRsaPrivateKey, KeyType.Rsa);
        PublicKey canonical = legacy.PublicKey.Clone();
        legacy.PublicKey.MergeFrom(new byte[] { 0x18, 0x01 });
        byte[] originalKey = legacy.PublicKey.ToByteArray();
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(legacy);
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue });
        await router.StartAsync(localPeer);
        List<Rpc> sent = [];
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion, new TaskCompletionSource().Task, sent.Add);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["legacy"], []));
        sent.Clear();

        router.Publish("legacy", [1, 2, 3]);
        // Outbound frames are written by the peer's asynchronous writer; drain it before reading what was sent.
        await router.FlushOutboundAsync();

        Message published = sent.SelectMany(r => r.Publish).Single();
        Assert.That(published.Key, Is.EqualTo(canonical.ToByteString()));
        Assert.That(published.From, Is.EqualTo(ByteString.CopyFrom(legacy.PeerId.Bytes)));
        Message unsigned = published.Clone();
        unsigned.ClearKey();
        unsigned.ClearSignature();
        Assert.That(legacy.VerifySignature([.. "libp2p-pubsub:"u8, .. unsigned.ToByteArray()], published.Signature.ToByteArray()), Is.True);
        Assert.That(published.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False);
        Assert.Throws<Libp2pException>(() => PublicKeyDecoder.Decode(published.Key));
        Assert.That(legacy.PublicKey.ToByteArray(), Is.EqualTo(originalKey));
        Assert.That(legacy.PrivateKey!.Data.ToByteArray(), Is.EqualTo(IdentityFixtures.LegacyRsaPrivateKey));
    }

    [Test]
    public void LargeRsaExponentCannotReachApplicationValidationOrDelivery()
    {
        Identity signer = new(keyType: KeyType.Rsa);
        PublicKey key = RsaKeyFixtures.EquivalentLargeExponent(signer);
        PeerId author = new((byte[])Multiformats.Hash.Multihash.Sum(Multiformats.Hash.HashType.SHA2_256, key.ToByteArray()));
        Rpc rpc = new Rpc().WithMessages("signed", 1, author.Bytes, [1], signer);
        rpc.Publish[0].Key = key.ToByteString();
        // This is a valid signature for the large exponent, not merely malformed input.
        Message unsigned = rpc.Publish[0].Clone();
        unsigned.ClearSignature();
        unsigned.ClearKey();
        byte[] payload = [.. "libp2p-pubsub:"u8, .. unsigned.ToByteArray()];
        var verifier = SignerUtilities.GetSigner("SHA256withRSA");
        verifier.Init(false, PublicKeyFactory.CreateKey(key.Data.ToByteArray()));
        verifier.BlockUpdate(payload, 0, payload.Length);
        Assert.That(verifier.VerifySignature(rpc.Publish[0].Signature.ToByteArray()), Is.True);
        using PubsubRouter router = new(new PeerStore());
        router.Subscribe("signed");
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, new TaskCompletionSource().Task, _ => { });
        int validations = 0, deliveries = 0;
        router.VerifyMessage = (_, _) => { validations++; return MessageValidity.Accepted; };
        router.OnMessage += (_, _, _) => deliveries++;
        router.OnRpc(TestPeers.PeerId(1), rpc);
        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.Zero);
            Assert.That(deliveries, Is.Zero);
            Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.LessThan(0));
        });
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
