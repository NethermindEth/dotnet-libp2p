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
        PeerRecordsVerificationPolicy policy = PeerRecordsVerificationPolicy.RequireWithWarning)
    {
        IProtocolStackSettings stack = Substitute.For<IProtocolStackSettings>();
        IdentifyProtocol protocol = new(stack, new IdentifyProtocolSettings
        {
            PeerRecordsVerificationPolicy = policy
        }, peerStore);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State
        {
            RemoteAddress = TestPeers.Multiaddr(remote),
            RemotePublicKey = remote.PublicKey
        });
        Channel channel = new();
        Task read = protocol.DialAsync(channel, context);
        await channel.Reverse.WriteSizeAndProtobufAsync(identify);
        await read;
    }
}
