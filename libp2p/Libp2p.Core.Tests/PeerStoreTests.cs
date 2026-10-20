// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using System.Buffers.Binary;
using Nethermind.Libp2p.Core.TestsBase;
using System.Reflection;

namespace Nethermind.Libp2p.Core.Tests;

/// <remarks>
/// Gossipsub passes peer-exchange records from remote peers straight to
/// <see cref="PeerStore.Discover(ByteString)"/>, so a bad record, whether its key
/// is unusable or its bytes are not a record at all, must be ignored rather than
/// throw out of the RPC handler.
/// </remarks>
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class PeerStoreTests
{
    [Test]
    public void RemoteDiscoveryCanFillAnAddressMissingFromAuthenticatedMetadata()
    {
        PeerStore store = new(1);
        store.SetSupportedProtocols(TestPeers.PeerId(1), ["/meshsub/1.3.0"]);

        store.Discover([TestPeers.Multiaddr(1)], isTrusted: false);

        Assert.That(store.GetPeerInfo(TestPeers.PeerId(1)).Addrs!.Select(a => a.ToString()), Is.EqualTo(new[] { TestPeers.Multiaddr(1).ToString() }));
    }

    [Test]
    public void RemoteAddressesCannotReplaceConfiguredAddresses()
    {
        PeerStore store = new(1);
        Multiaddress configured = TestPeers.Multiaddr(1);
        store.Discover([configured]);

        store.Discover([$"/ip4/192.0.2.1/tcp/9999/p2p/{TestPeers.PeerId(1)}"], isTrusted: false);
        store.Discover([$"/ip4/192.0.2.2/tcp/9999/p2p/{TestPeers.PeerId(2)}"], isTrusted: false);

        Assert.That(store.GetPeerInfo(TestPeers.PeerId(1)).Addrs!.Select(a => a.ToString()), Is.EqualTo(new[] { configured.ToString() }));
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
    }

    [Test]
    public void DisconnectedMetadataPublicationTargetsTheCurrentEntry()
    {
        PeerStore store = new(1);
        PeerStore.PeerInfo evicted = store.GetPeerInfo(TestPeers.PeerId(1));
        store.GetPeerInfo(TestPeers.PeerId(2));

        store.SetSupportedProtocols(TestPeers.PeerId(1), ["/meshsub/1.3.0"]);

        Assert.That(store.GetPeerInfo(TestPeers.PeerId(1)), Is.Not.SameAs(evicted));
        Assert.That(store.GetPeerInfo(TestPeers.PeerId(1)).SupportedProtocols, Is.EqualTo(new[] { "/meshsub/1.3.0" }));
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

        Assert.That(store.TryGetPeerInfo(known, out PeerStore.PeerInfo? retained), Is.True);
        Assert.That(retained!.Addrs!.Select(a => a.ToString()), Is.EqualTo(new[] { TestPeers.Multiaddr(2).ToString() }));
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
        foreach (PeerId id in ids) store.GetPeerInfo(id);

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
        PeerStore.PeerInfo original = store.GetPeerInfo(first);
        store.GetPeerInfo(second);
        for (int i = 0; i < 100; i++) Assert.That(store.GetPeerInfo(first), Is.SameAs(original));

        store.GetPeerInfo(TestPeers.PeerId(3));

        Assert.That(store.TryGetPeerInfo(first, out _), Is.False);
        Assert.That(store.TryGetPeerInfo(second, out _), Is.True);
        Assert.That(store.GetPeerInfo(first), Is.Not.SameAs(original));
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
    public void ConcurrentInsertionsStayWithinCapacity()
    {
        PeerStore store = new(4);
        PeerId[] ids = Enumerable.Range(1, 100).Select(TestPeers.PeerId).ToArray();

        Parallel.ForEach(ids, id => store.GetPeerInfo(id));

        Assert.That(ids.Count(id => store.TryGetPeerInfo(id, out _)), Is.EqualTo(4));
        PeerId last = TestPeers.PeerId(101);
        store.GetPeerInfo(last);
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
