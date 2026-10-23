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
    [TestCase(false)]
    [TestCase(true)]
    public async Task IdentifyAndPushCannotRollBackARecordLearnedFromPeerExchange(bool push)
    {
        Identity remote = TestPeers.Identity(81);
        PeerStore store = new();
        string currentAddress = $"/ip4/192.0.2.42/tcp/4042/p2p/{remote.PeerId}";
        ByteString current = SigningHelper.CreateSignedEnvelope(remote, [currentAddress], 42);
        store.Discover(current);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        ByteString stale = SigningHelper.CreateSignedEnvelope(remote, [TestPeers.Multiaddr(remote)], 41);
        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = stale },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: state);
        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(current));
        Assert.That(store.GetPeerInfo(remote.PeerId).Addrs!.Select(a => a.ToString()), Is.EqualTo(new[] { currentAddress }));
        Assert.That(state.VerifiedPeerRecordSequence, Is.EqualTo(42UL));
        ByteString next = SigningHelper.CreateSignedEnvelope(remote, [TestPeers.Multiaddr(remote)], 43);
        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = next },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: state);
        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(next));
        store.Discover(current);
        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(next));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LocallyOwnedProtocolIdsAreNotTruncatedOrRetainedFromTheWire(bool push)
    {
        Identity remote = TestPeers.Identity(82);
        PeerStore store = new();
        string localId = "/application/" + new string('x', 129);
        ProtocolStackSettings stack = new() { Protocols = new() { [new(new AdvertisedProtocol(localId))] = [] } };
        Identify message = new() { PublicKey = remote.PublicKey.ToByteString(), Protocols = { localId, new string('y', 129) } };

        await ReadIdentifyAsync(remote, store, message, push: push, stack: stack);

        string[] protocols = store.GetPeerInfo(remote.PeerId).SupportedProtocols!;
        Assert.That(protocols, Is.EqualTo(new[] { localId }));
        Assert.That(protocols[0], Is.SameAs(localId));
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 65)]
    [TestCase(true, 65)]
    public async Task StoredProtocolsPrioritizeLocalNegotiationBeforeRemoteOrdering(bool push, int extraLocalProtocols)
    {
        Identity remote = TestPeers.Identity(82);
        PeerStore store = new();
        string[] localProtocols = [.. Enumerable.Range(0, extraLocalProtocols).Select(i => $"/local-request/{i}"), "/meshsub/1.3.0", "/meshsub/1.2.0"];
        ProtocolStackSettings stack = new() { Protocols = localProtocols.ToDictionary(id => new ProtocolRef(new AdvertisedProtocol(id)), _ => Array.Empty<ProtocolRef>()) };
        Identify message = new() { PublicKey = remote.PublicKey.ToByteString() };
        message.Protocols.AddRange(Enumerable.Range(0, 64).Select(i => $"/unrelated/{i}"));
        message.Protocols.AddRange(localProtocols.Reverse());
        message.Protocols.Add("/meshsub/1.3.0");

        await ReadIdentifyAsync(remote, store, message, push: push, stack: stack);

        string[] protocols = store.GetPeerInfo(remote.PeerId).SupportedProtocols!;
        Assert.That(protocols.Take(localProtocols.Length), Is.EqualTo(localProtocols));
        for (int i = 0; i < localProtocols.Length; i++) Assert.That(protocols[i], Is.SameAs(localProtocols[i]));
        Assert.That(protocols.Length, Is.EqualTo(Math.Max(64, localProtocols.Length)));
        Assert.That(protocols.Distinct().Count(), Is.EqualTo(protocols.Length));
    }

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

    [TestCase(false, 63)]
    [TestCase(true, 63)]
    [TestCase(false, 64)]
    [TestCase(true, 64)]
    [TestCase(false, 65)]
    [TestCase(true, 65)]
    public async Task StoredProtocolCountIsBounded(bool push, int count)
    {
        Identity remote = TestPeers.Identity(84);
        PeerStore store = new();
        string[] protocols = Enumerable.Range(0, count).Select(i => $"/protocol/{i}").ToArray();
        await ReadIdentifyAsync(remote, store, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            Protocols = { protocols }
        }, push: push);

        Assert.That(store.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(protocols.Take(64)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StoredProtocolIdsAreBoundedInUtf8Bytes(bool push)
    {
        Identity remote = TestPeers.Identity(85);
        PeerStore store = new();
        string asciiAtLimit = new('a', 128);
        string unicodeAtLimit = new('é', 64);
        string[] expected = [new string('b', 127), asciiAtLimit, unicodeAtLimit, "/valid/1"];
        await ReadIdentifyAsync(remote, store, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            Protocols = { expected[0], new string('c', 129), asciiAtLimit, new string('é', 65), unicodeAtLimit, "/valid/1" }
        }, push: push);

        Assert.That(store.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(expected));
    }

    [TestCase(0ul)]
    [TestCase(1ul)]
    [TestCase(ulong.MaxValue)]
    public async Task StrictPushRemainsValidAfterPeerStoreEviction(ulong sequence)
    {
        Identity remote = TestPeers.Identity(86);
        PeerStore store = new(1);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        await ReadIdentifyAsync(remote, store, new Identify
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = SigningHelper.CreateSignedEnvelope(remote, [], sequence)
        }, PeerRecordsVerificationPolicy.RequireCorrect, state: state);
        store.GetPeerInfo(TestPeers.PeerId(87));
        Assert.That(store.TryGetPeerInfo(remote.PeerId, out _), Is.False);

        await ReadIdentifyAsync(remote, store, new Identify { Protocols = { "/valid/1" } },
            PeerRecordsVerificationPolicy.RequireCorrect, push: true, state: state);

        Assert.That(store.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/valid/1" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SignedRecordsAfterEvictionRejectOlderRestoreEqualAndAcceptNewer(bool push)
    {
        Identity remote = TestPeers.Identity(88);
        PeerStore store = new(1);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        string currentAddress = $"/ip4/192.0.2.42/tcp/4042/p2p/{remote.PeerId}";
        ByteString newer = SigningHelper.CreateSignedEnvelope(remote, [currentAddress], 42);
        ByteString older = SigningHelper.CreateSignedEnvelope(remote, [$"/ip4/192.0.2.1/tcp/4001/p2p/{remote.PeerId}"], 41);
        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = newer },
            PeerRecordsVerificationPolicy.RequireCorrect, state: state);
        store.GetPeerInfo(TestPeers.PeerId(89));
        Assert.That(store.TryGetPeerInfo(remote.PeerId, out _), Is.False);

        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = older },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: state);

        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.Null);
        Assert.That(store.GetPeerInfo(remote.PeerId).Addrs, Is.Null);

        int announcements = 0;
        store.OnNewPeer += _ => announcements++;
        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = newer },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: state);
        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(newer));
        Assert.That(store.GetPeerInfo(remote.PeerId).Seq, Is.EqualTo(42UL));
        Assert.That(store.GetPeerInfo(remote.PeerId).Addrs!.Select(a => a.ToString()), Is.EqualTo(new[] { currentAddress }));
        Assert.That(announcements, Is.EqualTo(1));

        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = newer },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: state);
        Assert.That(announcements, Is.EqualTo(1), "An equal record already in the store must not be republished.");

        ByteString latest = SigningHelper.CreateSignedEnvelope(remote, [$"/ip4/192.0.2.2/tcp/4002/p2p/{remote.PeerId}"], 43);
        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = latest },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: state);
        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(latest));
        Assert.That(store.GetPeerInfo(remote.PeerId).Seq, Is.EqualTo(43UL));
        Assert.That(store.GetPeerInfo(remote.PeerId).Addrs!.Select(a => a.ToString()),
            Is.EqualTo(new[] { $"/ip4/192.0.2.2/tcp/4002/p2p/{remote.PeerId}" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EqualSessionSequenceReplacesAnOlderRecordFromAnotherState(bool push)
    {
        Identity remote = TestPeers.Identity(90);
        PeerStore store = new(1);
        State first = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        State second = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        string currentAddress = $"/ip4/192.0.2.42/tcp/4042/p2p/{remote.PeerId}";
        ByteString current = SigningHelper.CreateSignedEnvelope(remote, [currentAddress], 42);
        ByteString older = SigningHelper.CreateSignedEnvelope(remote, [$"/ip4/192.0.2.41/tcp/4041/p2p/{remote.PeerId}"], 41);
        store.Discover(current, first);
        store.GetPeerInfo(TestPeers.PeerId(91));
        store.Discover(older, second);
        Assert.That(store.GetPeerInfo(remote.PeerId).Seq, Is.EqualTo(41UL));
        Assert.That(first.VerifiedPeerRecordSequence, Is.EqualTo(42UL));

        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), SignedPeerRecord = current },
            PeerRecordsVerificationPolicy.RequireCorrect, push: push, state: first);

        PeerStore.PeerInfo info = store.GetPeerInfo(remote.PeerId);
        Assert.That(info.Seq, Is.EqualTo(42UL));
        Assert.That(info.SignedPeerRecord, Is.EqualTo(current));
        Assert.That(info.Addrs!.Select(a => a.ToString()), Is.EqualTo(new[] { currentAddress }));
        Assert.That(first.VerifiedPeerRecordSequence, Is.EqualTo(42UL));
    }

    /// <remarks>
    /// The handshake stored the key compressed; the peer announces the same key
    /// uncompressed in Identify, which must still match. The key is the go-generated
    /// js-libp2p fixture also used by <c>PublicKeyDecoderTests</c>.
    /// </remarks>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AnnouncedKeyMatches_WhenSecp256k1KeyArrivesUncompressed(bool push)
    {
        PublicKey compressed = new() { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(Convert.FromHexString("029c0ce5d53646ed47112560297a3e59b78b8cbd4bae37c7a0c236eeb91d0fbeaf")) };
        PublicKey uncompressed = new()
        {
            Type = KeyType.Secp256K1,
            Data = ByteString.CopyFrom(Convert.FromHexString(
                "049c0ce5d53646ed47112560297a3e59b78b8cbd4bae37c7a0c236eeb91d0fbeaf698f4ac1643c2fd5bd0b9588df903f5310637e96942b4b22916a4cb4cdf6c990")),
        };
        Assert.That(PublicKeyDecoder.Decode(uncompressed.ToByteString()), Is.EqualTo(compressed), "precondition: both are encodings of one key");
        Identity remote = new(compressed);
        PeerStore store = new();

        await ReadIdentifyAsync(remote, store, new Identify { PublicKey = uncompressed.ToByteString(), Protocols = { "/valid/1" } }, push: push);

        Assert.That(store.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/valid/1" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnnouncedKeyIsRejected_WhenItIsAPaddedCopyOfTheSessionKey(bool push)
    {
        Identity remote = TestPeers.Identity(90);
        PublicKey padded = new() { Type = KeyType.Ed25519, Data = ByteString.CopyFrom([.. remote.PublicKey.Data.ToByteArray(), 0x00]) };

        Assert.ThrowsAsync<PeerConnectionException>(() => ReadIdentifyAsync(remote, new PeerStore(), new Identify { PublicKey = padded.ToByteString() }, push: push));
    }

    private static async Task ReadIdentifyAsync(Identity remote, PeerStore peerStore, Identify identify,
        PeerRecordsVerificationPolicy policy = PeerRecordsVerificationPolicy.RequireWithWarning, bool push = false, State? state = null, IProtocolStackSettings? stack = null)
    {
        stack ??= Substitute.For<IProtocolStackSettings>();
        IdentifyProtocolSettings settings = new()
        {
            PeerRecordsVerificationPolicy = policy
        };
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(state ?? new State
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

    private sealed class AdvertisedProtocol(string id) : ISessionListenerProtocol
    {
        public string Id => id;
        public Task ListenAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
    }
}
