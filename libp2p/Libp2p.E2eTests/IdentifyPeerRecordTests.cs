// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using NUnit.Framework;
using NSubstitute;

namespace Libp2p.E2eTests;

public class IdentifyPeerRecordTests
{
    [Test]
    public async Task MissingOptionalPeerRecordDoesNotCreateOne()
    {
        Identity remote = TestPeers.Identity(81);
        PeerStore peerStore = new();
        Identify identify = new() { PublicKey = remote.PublicKey.ToByteString() };
        identify.Protocols.Add("/meshsub/1.2.0");

        await ReadIdentifyAsync(remote, peerStore, identify);

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remote.PeerId);
        Assert.That(peerInfo.SupportedProtocols, Is.EqualTo(new[] { "/meshsub/1.2.0" }));
        Assert.That(peerInfo.SignedPeerRecord, Is.Null);
        Assert.That(peerInfo.Seq, Is.Null);
    }

    [Test]
    public async Task InvalidPeerRecordIsNotCached()
    {
        Identity remote = TestPeers.Identity(82);
        PeerStore peerStore = new();
        Identify identify = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = ByteString.Empty
        };

        await ReadIdentifyAsync(remote, peerStore, identify);

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remote.PeerId);
        Assert.That(peerInfo.SignedPeerRecord, Is.Null);
        Assert.That(peerInfo.Seq, Is.Null);
    }

    [Test]
    public async Task MalformedPeerRecordDoesNotPreventIdentify()
    {
        Identity remote = TestPeers.Identity(84);
        PeerStore peerStore = new();
        Identify identify = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = ByteString.CopyFrom([0x0A, 0x02, 0x01])
        };

        await ReadIdentifyAsync(remote, peerStore, identify);

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remote.PeerId);
        Assert.That(peerInfo.SignedPeerRecord, Is.Null);
        Assert.That(peerInfo.Seq, Is.Null);
    }

    [Test]
    public async Task ShortSignatureDoesNotPreventIdentify()
    {
        Identity remote = TestPeers.Identity(86);
        PeerStore peerStore = new();
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SigningHelper.CreateSignedEnvelope(remote, [], 1));
        envelope.Signature = ByteString.CopyFrom([1, 2, 3]);
        Identify identify = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = envelope.ToByteString()
        };

        await ReadIdentifyAsync(remote, peerStore, identify);

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remote.PeerId);
        Assert.That(peerInfo.SignedPeerRecord, Is.Null);
        Assert.That(peerInfo.Seq, Is.Null);
    }

    [Test]
    public void StrictPolicyRejectsMissingPeerRecord()
    {
        Identity remote = TestPeers.Identity(85);
        PeerStore peerStore = new();
        Identify identify = new() { PublicKey = remote.PublicKey.ToByteString() };

        PeerConnectionException? exception = Assert.ThrowsAsync<PeerConnectionException>(async () =>
            await ReadIdentifyAsync(remote, peerStore, identify, PeerRecordsVerificationPolicy.RequireCorrect));
        Assert.That(exception?.Message, Does.Contain("there is no peer record"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StrictPolicyRejectsPeerRecordWithDifferentEnvelopeKey(bool push)
    {
        Identity remote = TestPeers.Identity(87);
        PeerStore peerStore = new();
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SigningHelper.CreateSignedEnvelope(remote, [], 1));
        envelope.PublicKey = TestPeers.Identity(88).PublicKey.ToByteString();
        Identify identify = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = envelope.ToByteString()
        };

        Assert.ThrowsAsync<PeerConnectionException>(async () =>
            await ReadIdentifyAsync(remote, peerStore, identify, PeerRecordsVerificationPolicy.RequireCorrect, push));
        Assert.That(peerStore.TryGetPeerInfo(remote.PeerId, out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StrictPolicyRejectsPeerRecordWithAlteredPayloadType(bool push)
    {
        Identity remote = TestPeers.Identity(89);
        PeerStore peerStore = new();
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SigningHelper.CreateSignedEnvelope(remote, [], 1));
        byte[] payloadType = envelope.PayloadType.ToByteArray();
        envelope.PayloadType = ByteString.CopyFrom([.. payloadType, 0x01]);
        Identify identify = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = envelope.ToByteString()
        };

        Assert.ThrowsAsync<PeerConnectionException>(async () =>
            await ReadIdentifyAsync(remote, peerStore, identify, PeerRecordsVerificationPolicy.RequireCorrect, push));
        Assert.That(peerStore.TryGetPeerInfo(remote.PeerId, out _), Is.False);
    }

    [Test]
    public async Task SignedPayloadTypeSuffixIsAcceptedWhenSignatureMatches()
    {
        Identity remote = TestPeers.Identity(90);
        PeerStore peerStore = new();
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SigningHelper.CreateSignedEnvelope(remote, [], 7));
        byte[] payloadType = [.. envelope.PayloadType.Span, 0x01];
        envelope.PayloadType = ByteString.CopyFrom(payloadType);

        byte[] domain = "libp2p-peer-record"u8.ToArray();
        byte[] signingData = new byte[
            VarInt.GetSizeInBytes(domain.Length) + domain.Length +
            VarInt.GetSizeInBytes(payloadType.Length) + payloadType.Length +
            VarInt.GetSizeInBytes(envelope.Payload.Length) + envelope.Payload.Length];
        int offset = 0;
        VarInt.Encode(domain.Length, signingData.AsSpan(), ref offset);
        domain.CopyTo(signingData.AsSpan(offset));
        offset += domain.Length;
        VarInt.Encode(payloadType.Length, signingData.AsSpan(), ref offset);
        payloadType.CopyTo(signingData.AsSpan(offset));
        offset += payloadType.Length;
        VarInt.Encode(envelope.Payload.Length, signingData.AsSpan(), ref offset);
        envelope.Payload.Span.CopyTo(signingData.AsSpan(offset));
        envelope.Signature = ByteString.CopyFrom(remote.Sign(signingData));

        ByteString signedRecord = envelope.ToByteString();
        await ReadIdentifyAsync(remote, peerStore, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = signedRecord
        }, PeerRecordsVerificationPolicy.RequireCorrect);

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remote.PeerId);
        Assert.That(peerInfo.SignedPeerRecord, Is.EqualTo(signedRecord));
        Assert.That(peerInfo.Seq, Is.EqualTo(7UL));
    }

    [Test]
    public async Task OlderOrInvalidPeerRecordDoesNotReplaceNewerRecord()
    {
        Identity remote = TestPeers.Identity(83);
        PeerStore peerStore = new();
        ByteString newerRecord = SigningHelper.CreateSignedEnvelope(remote, [], 42);
        ByteString olderRecord = SigningHelper.CreateSignedEnvelope(remote, [], 41);

        await ReadIdentifyAsync(remote, peerStore, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = newerRecord,
            Protocols = { "/meshsub/1.1.0" }
        });
        await ReadIdentifyAsync(remote, peerStore, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = olderRecord,
            Protocols = { "/meshsub/1.2.0" }
        });
        await ReadIdentifyAsync(remote, peerStore, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = ByteString.Empty,
            Protocols = { "/meshsub/1.3.0" }
        });

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remote.PeerId);
        Assert.That(peerInfo.SignedPeerRecord, Is.EqualTo(newerRecord));
        Assert.That(peerInfo.Seq, Is.EqualTo(42UL));
        Assert.That(peerInfo.SupportedProtocols, Is.EqualTo(new[] { "/meshsub/1.3.0" }));
    }

    private static async Task ReadIdentifyAsync(Identity remote, PeerStore peerStore, Identify identify,
        PeerRecordsVerificationPolicy policy = PeerRecordsVerificationPolicy.RequireWithWarning, bool push = false)
    {
        IProtocolStackSettings stack = Substitute.For<IProtocolStackSettings>();
        IdentifyProtocolSettings settings = new()
        {
            PeerRecordsVerificationPolicy = policy
        };
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State
        {
            RemoteAddress = TestPeers.Multiaddr(remote),
            RemotePublicKey = remote.PublicKey
        });
        Channel channel = new();
        Task read = push
            ? new IdentifyPushProtocol(stack, settings, peerStore).ListenAsync(channel, context)
            : new IdentifyProtocol(stack, settings, peerStore).DialAsync(channel, context);
        await channel.Reverse.WriteSizeAndProtobufAsync(identify);
        await read;
    }
}
