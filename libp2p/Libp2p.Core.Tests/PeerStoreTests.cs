// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using System.Buffers.Binary;
using Nethermind.Libp2p.Core.TestsBase;
using System.Reflection;
using Org.BouncyCastle.Crypto.EC;

namespace Nethermind.Libp2p.Core.Tests;

/// <remarks>
/// Gossipsub passes peer-exchange records from remote peers straight to
/// <see cref="IPeerStore.Discover(ByteString, State)"/>, so a bad record, whether its key
/// is unusable or its bytes are not a record at all, must be ignored rather than
/// throw out of the RPC handler.
/// </remarks>
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class PeerStoreTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void DiscoveryStoresAndAnnouncesOnlyAddressesForItsPeer(bool isTrusted, bool bareFirst)
    {
        PeerStore store = new();
        Multiaddress valid = TestPeers.Multiaddr(1);
        Multiaddress bare = "/ip4/192.0.2.1/tcp/4001";
        Multiaddress foreign = TestPeers.Multiaddr(2);
        List<Multiaddress[]> announcements = [];
        store.OnNewPeer += announcements.Add;

        store.Discover(bareFirst ? [bare, valid, foreign] : [valid, bare, foreign], isTrusted);

        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(1), out var info), Is.True);
        Assert.That(info!.Addresses, Is.EquivalentTo(new[] { valid }));
        Assert.That(announcements, Has.Count.EqualTo(1));
        Assert.That(announcements[0], Is.EqualTo(new[] { valid }));
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
    }

    /// <summary>
    /// A peer that keeps the address it was first seen on and starts listening on another -- QUIC
    /// beside an existing TCP port -- has to reach the store as the wider set. Discovery callers
    /// must not pre-filter on one address, which is why this comparison lives here.
    /// </summary>
    [Test]
    public void RediscoveryAddsAnAddressAlongsideTheOneAlreadyKnown()
    {
        PeerStore store = new();
        string peerId = TestPeers.PeerId(1).ToString();
        Multiaddress tcp = $"/ip4/192.0.2.10/tcp/4001/p2p/{peerId}";
        Multiaddress quic = $"/ip4/192.0.2.10/udp/4001/quic-v1/p2p/{peerId}";
        List<Multiaddress[]> announcements = [];

        store.Discover([tcp], isTrusted: false);
        store.OnNewPeer += announcements.Add;
        store.Discover([tcp, quic], isTrusted: false);

        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(1), out var info), Is.True);
        Assert.That(info!.Addresses, Is.EquivalentTo(new[] { tcp, quic }),
            "the unchanged first address must not mask the new one");

        // The same set again is a no-op, which is what lets callers re-announce freely.
        store.Discover([quic, tcp], isTrusted: false);
        Assert.That(store.ReadPeerInfo(TestPeers.PeerId(1)), Is.SameAs(info));
    }

    [Test]
    public void MetadataDoesNotPreventRemoteAddressRefresh()
    {
        PeerStore store = new(1);
        PeerId peerId = TestPeers.PeerId(1);
        Multiaddress updated = $"/ip4/192.0.2.20/tcp/4001/p2p/{peerId}";
        store.Discover([$"/ip4/192.0.2.10/tcp/4001/p2p/{peerId}"], isTrusted: false);
        store.SetSupportedProtocols(peerId, ["/meshsub/1.3.0"]);
        List<Multiaddress[]> announcements = [];
        store.OnNewPeer += announcements.Add;
        announcements.Clear();

        store.Discover([updated], isTrusted: false);

        Assert.That(store.ReadPeerInfo(peerId).Addresses, Is.EquivalentTo(new[] { updated }));
        Assert.That(announcements, Has.Count.EqualTo(1));
        Assert.That(announcements[0], Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void AgentVersionPublicationUsesTheCurrentEntryWithoutAcquiringConfiguredTrust()
    {
        PeerStore store = new(1);
        PeerId peerId = TestPeers.PeerId(1);
        store.SetSupportedProtocols(peerId, ["/old/1"]);
        PeerMetadata evicted = store.ReadPeerInfo(peerId);
        store.SetAgentVersion(TestPeers.PeerId(2), null);

        store.SetAgentVersion(peerId, "client/1");

        PeerMetadata current = store.ReadPeerInfo(peerId);
        Assert.That(current, Is.Not.SameAs(evicted));
        Assert.That(current.AgentVersion, Is.EqualTo("client/1"));
        Assert.That(evicted.AgentVersion, Is.Null);
        Multiaddress first = $"/ip4/192.0.2.10/tcp/4001/p2p/{peerId}";
        Multiaddress updated = $"/ip4/192.0.2.20/tcp/4001/p2p/{peerId}";
        store.Discover([first], isTrusted: false);
        store.Discover([updated], isTrusted: false);
        Assert.That(store.ReadPeerInfo(peerId).Addresses, Is.EquivalentTo(new[] { updated }));
    }

    [Test]
    public void UnsignedDiscoveryCannotReplaceSignedAddresses()
    {
        Identity remote = TestPeers.Identity(1);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        PeerStore store = new(1);
        store.Discover(SignedRecord(remote), state);

        store.Discover([$"/ip4/192.0.2.20/tcp/4001/p2p/{remote.PeerId}"], isTrusted: false);

        Assert.That(store.ReadPeerInfo(remote.PeerId).Addresses,
            Is.EquivalentTo(new Multiaddress[] { $"/ip4/127.0.0.1/tcp/4001/p2p/{remote.PeerId}" }));
    }

    [Test]
    public void RemoteDiscoveryCanFillAnAddressMissingFromAuthenticatedMetadata()
    {
        PeerStore store = new(1);
        store.SetSupportedProtocols(TestPeers.PeerId(1), ["/meshsub/1.3.0"]);

        store.Discover([TestPeers.Multiaddr(1)], isTrusted: false);

        Assert.That(store.ReadPeerInfo(TestPeers.PeerId(1)).Addresses!.Select(a => a.ToString()), Is.EqualTo(new[] { TestPeers.Multiaddr(1).ToString() }));
    }

    [Test]
    public void RemoteAddressesCannotReplaceConfiguredAddresses()
    {
        PeerStore store = new(1);
        Multiaddress configured = TestPeers.Multiaddr(1);
        store.Discover([configured]);

        store.Discover([$"/ip4/192.0.2.1/tcp/9999/p2p/{TestPeers.PeerId(1)}"], isTrusted: false);
        store.Discover([$"/ip4/192.0.2.2/tcp/9999/p2p/{TestPeers.PeerId(2)}"], isTrusted: false);

        Assert.That(store.ReadPeerInfo(TestPeers.PeerId(1)).Addresses!.Select(a => a.ToString()), Is.EqualTo(new[] { configured.ToString() }));
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
    }

    [Test]
    public void DisconnectedMetadataPublicationTargetsTheCurrentEntry()
    {
        PeerStore store = new(1);
        store.SetAgentVersion(TestPeers.PeerId(1), "old/1");
        PeerMetadata evicted = store.ReadPeerInfo(TestPeers.PeerId(1));
        store.SetAgentVersion(TestPeers.PeerId(2), null);

        store.SetSupportedProtocols(TestPeers.PeerId(1), ["/meshsub/1.3.0"]);

        Assert.That(store.ReadPeerInfo(TestPeers.PeerId(1)), Is.Not.SameAs(evicted));
        Assert.That(store.ReadPeerInfo(TestPeers.PeerId(1)).SupportedProtocols, Is.EqualTo(new[] { "/meshsub/1.3.0" }));
        Assert.That(evicted.SupportedProtocols, Is.Null);
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
    }

    [Test]
    public void PeerExchangeCannotDisplaceConfiguredSeeds()
    {
        PeerStore store = new(2);
        store.Discover([TestPeers.Multiaddr(1)]);
        Identity[] exchanged = Enumerable.Range(2, 8).Select(TestPeers.Identity).ToArray();

        foreach (Identity peer in exchanged) store.Discover(SignedRecord(peer));

        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(1), out _), Is.True);
        Assert.That(exchanged.Count(peer => store.TryGetPeerInfo(peer.PeerId, out _)), Is.EqualTo(1));
    }

    [Test]
    public void SessionRecordCallbacksRunOutsidePublicationLocks()
    {
        Identity remote = TestPeers.Identity(8);
        PeerStore store = new(1);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(remote), RemotePublicKey = remote.PublicKey };
        Lock storeLock = (Lock)typeof(PeerStore).GetField("_storeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        string address = $"/ip4/192.0.2.1/tcp/4001/p2p/{remote.PeerId}";
        bool announced = false;
        store.OnNewPeer += addresses =>
        {
            announced = true;
            Assert.That(addresses.Select(a => a.ToString()), Is.EqualTo(new[] { address }));
            Assert.That(store.TryGetPeerInfo(remote.PeerId, out _), Is.True);
            bool locksAvailable = Task.Run(() =>
            {
                if (!state.PeerRecordLock.TryEnter()) return false;
                try
                {
                    if (!storeLock.TryEnter()) return false;
                    storeLock.Exit();
                    return true;
                }
                finally
                {
                    state.PeerRecordLock.Exit();
                }
            }).GetAwaiter().GetResult();
            Assert.That(locksAvailable, Is.True, "Subscribers must be able to inspect session and peer metadata from another thread.");
        };

        store.Discover(SigningHelper.CreateSignedEnvelope(remote, [address], 42), state);

        Assert.That(announced, Is.True);
        Assert.That(state.VerifiedPeerRecordSequence, Is.EqualTo(42UL));
    }

    [Test]
    public void DialingUnknownPeerDoesNotEvictKnownPeer()
    {
        PeerStore store = new(1);
        PeerId known = TestPeers.PeerId(2);
        store.Discover([TestPeers.Multiaddr(2)]);
        LocalPeer peer = new(TestPeers.Identity(1), store, new ProtocolStackSettings());

        Assert.ThrowsAsync<Exceptions.Libp2pException>(async () => await peer.DialAsync(TestPeers.PeerId(3)));

        Assert.That(store.TryGetPeerInfo(known, out PeerMetadata? retained), Is.True);
        Assert.That(retained!.Addresses!.Select(a => a.ToString()), Is.EqualTo(new[] { TestPeers.Multiaddr(2).ToString() }));
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(3), out _), Is.False);
    }

    [Test]
    public void DefaultCapacityEvictsTheOldestPeer()
    {
        PeerStore store = new();
        PeerId[] ids = Enumerable.Range(0, 10_001).Select(i =>
        {
            byte[] hash = new byte[34];
            hash[0] = 0x12;
            hash[1] = 0x20;
            BinaryPrimitives.WriteInt32LittleEndian(hash.AsSpan(30), i);
            return new PeerId(hash);
        }).ToArray();
        foreach (PeerId id in ids) store.SetAgentVersion(id, null);

        Assert.That(store.TryGetPeerInfo(ids[0], out _), Is.False, "The store must not retain every peer indefinitely.");
        Assert.That(store.TryGetPeerInfo(ids[1], out _), Is.True);
        Assert.That(store.TryGetPeerInfo(ids[^1], out _), Is.True);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void CapacityMustBePositive(int capacity) => Assert.Throws<ArgumentOutOfRangeException>(() => new PeerStore(capacity));

    [Test]
    public void ExistingLookupsPreserveEntryAndInsertionOrder()
    {
        PeerStore store = new(2);
        PeerId first = TestPeers.PeerId(1);
        PeerId second = TestPeers.PeerId(2);
        store.SetAgentVersion(first, "first/1");
        PeerMetadata original = store.ReadPeerInfo(first);
        store.SetAgentVersion(second, "second/1");
        for (int i = 0; i < 100; i++) Assert.That(store.ReadPeerInfo(first), Is.SameAs(original));

        store.SetAgentVersion(TestPeers.PeerId(3), null);

        Assert.That(store.TryGetPeerInfo(first, out _), Is.False);
        Assert.That(store.TryGetPeerInfo(second, out _), Is.True);
        store.SetAgentVersion(first, "first/2");
        Assert.That(store.ReadPeerInfo(first).AgentVersion, Is.EqualTo("first/2"));
        Assert.That(store.TryGetPeerInfo(second, out _), Is.False);
    }

    [Test]
    public void DiscoveryUsesTheSameCapacityAndReplaysOnlyRetainedPeers()
    {
        PeerStore store = new(2);
        store.Discover([TestPeers.Multiaddr(1)]);
        store.Discover([TestPeers.Multiaddr(2)]);
        store.Discover([TestPeers.Multiaddr(3)]);
        List<PeerId?> replayed = [];
        store.OnNewPeer += addrs => replayed.Add(addrs[0].GetPeerId());

        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(1), out _), Is.False);
        Assert.That(replayed, Is.EquivalentTo(new[] { TestPeers.PeerId(2), TestPeers.PeerId(3) }));
    }

    [Test]
    public void MetadataReadsAreNonCreatingAndReturnReadOnlySnapshots()
    {
        IPeerStore store = new PeerStore(1);
        PeerId peerId = TestPeers.PeerId(1);
        Multiaddress first = TestPeers.Multiaddr(1);
        Multiaddress[] addresses = [first];
        string[] protocols = ["/example/1"];
        store.Discover(addresses, isTrusted: false);
        store.SetSupportedProtocols(peerId, protocols);
        addresses[0] = TestPeers.Multiaddr(2);
        protocols[0] = "/changed";

        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out PeerMetadata? missing), Is.False);
        Assert.That(missing, Is.Null);
        PeerMetadata initial = store.ReadPeerInfo(peerId);
        Assert.That(initial.Addresses, Is.EqualTo(new[] { first }));
        Assert.That(initial.SupportedProtocols, Is.EqualTo(new[] { "/example/1" }));
        Assert.Throws<NotSupportedException>(() => ((IList<Multiaddress>)initial.Addresses!)[0] = addresses[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)initial.SupportedProtocols!)[0] = "/changed");

        Multiaddress updated = $"/ip4/192.0.2.2/tcp/4040/p2p/{peerId}";
        store.Discover([updated], isTrusted: false);
        store.SetSupportedProtocols(peerId, ["/example/2"]);
        PeerMetadata current = store.ReadPeerInfo(peerId);

        Assert.That(current.Addresses, Is.EqualTo(new[] { updated }));
        Assert.That(current.SupportedProtocols, Is.EqualTo(new[] { "/example/2" }));
        Assert.That(initial.Addresses, Is.EqualTo(new[] { first }));
        Assert.That(initial.SupportedProtocols, Is.EqualTo(new[] { "/example/1" }));
    }

    [Test]
    public void NewSubscribersReplayRetainedAddressesAndObserveAddressChangesUntilRemoved()
    {
        IPeerStore store = new PeerStore(2);
        Multiaddress first = TestPeers.Multiaddr(1);
        Multiaddress updated = $"/ip4/192.0.2.3/tcp/4040/p2p/{TestPeers.PeerId(1)}";
        store.Discover([first], isTrusted: false);
        store.SetAgentVersion(TestPeers.PeerId(2), "metadata-only/1");

        List<Multiaddress[]> notifications = [];
        store.OnNewPeer += notifications.Add;
        Assert.That(notifications.Select(addrs => addrs.Single()), Is.EqualTo(new[] { first }));

        store.SetAgentVersion(TestPeers.PeerId(1), "peer/1");
        store.Discover([first], isTrusted: false);
        store.Discover([updated], isTrusted: false);
        store.OnNewPeer -= notifications.Add;
        store.Discover([first], isTrusted: false);

        Assert.That(notifications.Select(addrs => addrs.Single()), Is.EqualTo(new[] { first, updated }));
    }

    [Test]
    public void ConcurrentInsertionsStayWithinCapacity()
    {
        PeerStore store = new(4);
        PeerId[] ids = Enumerable.Range(1, 100).Select(TestPeers.PeerId).ToArray();

        Parallel.ForEach(ids, id => store.SetAgentVersion(id, null));

        Assert.That(ids.Count(id => store.TryGetPeerInfo(id, out _)), Is.EqualTo(4));
        PeerId last = TestPeers.PeerId(101);
        store.SetAgentVersion(last, null);
        Assert.That(store.TryGetPeerInfo(last, out _), Is.True);
        Assert.That(ids.Count(id => store.TryGetPeerInfo(id, out _)), Is.EqualTo(3));
    }

    [Test]
    public void Test_Discover_AnnouncesPeer_WhenRecordIsValid()
    {
        Identity peer = new();

        Assert.That(Announcements(SignedRecord(peer)), Is.EqualTo(1));
    }

    [TestCaseSource(nameof(UnusableKeys))]
    public void Test_Discover_IgnoresRecord_WhenKeyIsUnusable(PublicKey key)
    {
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SignedRecord(new Identity()));
        envelope.PublicKey = key.ToByteString();

        Assert.That(Announcements(envelope.ToByteString()), Is.Zero);
    }

    public static IEnumerable<TestCaseData> UnusableKeys()
    {
        yield return new TestCaseData(Key((KeyType)7, [1, 2, 3])).SetName("Test_Discover_IgnoresRecord_WhenKeyIsOfUnknownType");
        yield return new TestCaseData(Key(KeyType.Ed25519, new byte[31])).SetName("Test_Discover_IgnoresRecord_WhenEd25519KeyIsTruncated");
        yield return new TestCaseData(Key(KeyType.Secp256K1, [0x00])).SetName("Test_Discover_IgnoresRecord_WhenSecp256k1KeyIsThePointAtInfinity");
        yield return new TestCaseData(Key(KeyType.Rsa, [1, 2, 3, 4])).SetName("Test_Discover_IgnoresRecord_WhenRsaKeyIsGarbage");
    }

    /// <remarks>
    /// A padded Ed25519 key verifies its owner's signatures but hashes to another
    /// peer ID, so a record claiming that ID must not add a second peer.
    /// </remarks>
    [Test]
    public void Test_Discover_IgnoresRecord_WhenEd25519KeyIsPadded()
    {
        Identity peer = new();
        PublicKey padded = Key(KeyType.Ed25519, [.. peer.PublicKey.Data.ToByteArray(), 0x00]);
        PeerId paddedPeerId = new(Multiformats.Hash.Multihash.Encode(padded.ToByteArray(), Multiformats.Hash.HashType.ID));
        SignedEnvelope template = SignedEnvelope.Parser.ParseFrom(SignedRecord(peer));
        template.PublicKey = padded.ToByteString();
        PeerRecord record = new() { PeerId = ByteString.CopyFrom(paddedPeerId.Bytes), Seq = 1 };
        record.Addresses.Add(new AddressInfo { Multiaddr = ByteString.CopyFrom(Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{paddedPeerId}").ToBytes()) });

        Assert.That(Announcements(SignedByPeer(peer, template.ToByteString(), record.ToByteString())), Is.Zero);
    }

    /// <remarks>
    /// The record names the peer's usual (compressed) peer ID while its envelope
    /// carries the same key uncompressed, which still decodes to that peer.
    /// </remarks>
    [Test]
    public void Test_Discover_StoresRecord_UnderTheCompressedPeerId_WhenSecp256k1KeyIsUncompressed()
    {
        Identity peer = new(keyType: KeyType.Secp256K1);
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SignedRecord(peer));
        byte[] point = CustomNamedCurves.GetByName("secp256k1").Curve.DecodePoint(peer.PublicKey.Data.ToByteArray()).GetEncoded(false);
        envelope.PublicKey = Key(KeyType.Secp256K1, point).ToByteString();
        PeerStore store = new();

        store.Discover(envelope.ToByteString());

        Assert.That(store.TryGetPeerInfo(peer.PeerId, out _), Is.True);
    }

    [TestCaseSource(nameof(MalformedRecords))]
    public void Test_Discover_IgnoresRecord_WhenItIsMalformed(ByteString record)
    {
        Assert.That(Announcements(record), Is.Zero);
    }

    /// <remarks>
    /// The last case passes signature verification: its sender signed a payload
    /// that is not a peer record.
    /// </remarks>
    public static IEnumerable<TestCaseData> MalformedRecords()
    {
        Identity peer = new();
        ByteString valid = SignedRecord(peer);
        SignedEnvelope garbageKey = SignedEnvelope.Parser.ParseFrom(valid);
        garbageKey.PublicKey = ByteString.CopyFrom([0xff, 0xff, 0xff]);

        yield return new TestCaseData(ByteString.CopyFrom([0xff, 0xff, 0xff, 0xff])).SetName("Test_Discover_IgnoresRecord_WhenItIsNotProtobuf");
        yield return new TestCaseData(ByteString.CopyFrom(valid.ToByteArray()[..(valid.Length / 2)])).SetName("Test_Discover_IgnoresRecord_WhenEnvelopeIsTruncated");
        yield return new TestCaseData(garbageKey.ToByteString()).SetName("Test_Discover_IgnoresRecord_WhenEnvelopeKeyIsNotProtobuf");
        yield return new TestCaseData(SignedByPeer(peer, valid, ByteString.CopyFrom([0xff, 0xff, 0xff, 0xff]))).SetName("Test_Discover_IgnoresRecord_WhenSignedPayloadIsNotAPeerRecord");
    }

    private static int Announcements(ByteString signedRecord)
    {
        PeerStore store = new();
        int announced = 0;
        store.OnNewPeer += _ => announced++;

        store.Discover(signedRecord);

        return announced;
    }

    private static ByteString SignedRecord(Identity peer) =>
        SigningHelper.CreateSignedEnvelope(peer, [Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{peer.PeerId}")], 1);

    /// <summary>
    /// <paramref name="template"/>'s envelope with <paramref name="payload"/>, validly signed by
    /// <paramref name="peer"/> over the same domain and framing <see cref="SigningHelper"/> uses.
    /// </summary>
    private static ByteString SignedByPeer(Identity peer, ByteString template, ByteString payload)
    {
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(template);
        envelope.Payload = payload;
        byte[] signed = [.. LengthPrefixed("libp2p-peer-record"u8.ToArray()), .. LengthPrefixed(envelope.PayloadType.ToByteArray()), .. LengthPrefixed(payload.ToByteArray())];
        envelope.Signature = ByteString.CopyFrom(peer.Sign(signed));
        return envelope.ToByteString();
    }

    private static byte[] LengthPrefixed(byte[] bytes)
    {
        byte[] prefix = new byte[VarInt.GetSizeInBytes(bytes.Length)];
        int offset = 0;
        VarInt.Encode(bytes.Length, prefix, ref offset);
        return [.. prefix, .. bytes];
    }

    private static PublicKey Key(KeyType keyType, byte[] data) =>
        new() { Type = keyType, Data = ByteString.CopyFrom(data) };
}
