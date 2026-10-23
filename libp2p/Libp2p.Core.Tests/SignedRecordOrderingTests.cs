// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.TestsBase;
using System.Reflection;

namespace Nethermind.Libp2p.Core.Tests;

public class SignedRecordOrderingTests
{
    [TestCase("empty", false)]
    [TestCase("empty", true)]
    [TestCase("bare", false)]
    [TestCase("bare", true)]
    [TestCase("foreign", false)]
    [TestCase("foreign", true)]
    public void MetadataOnlyRecordsAllowUnsignedAddressDiscoveryAndRefresh(string shape, bool existingAddresses)
    {
        Identity peer = TestPeers.Identity(1);
        PeerStore store = new();
        Multiaddress[] recordAddresses = shape switch
        {
            "bare" => ["/ip4/192.0.2.1/tcp/4001"],
            "foreign" => [TestPeers.Multiaddr(2)],
            _ => [],
        };
        if (existingAddresses) store.Discover([Address(peer, 1)], isTrusted: false);
        List<Multiaddress[]> notifications = [];
        store.OnNewPeer += notifications.Add;
        notifications.Clear();
        ByteString record = SigningHelper.CreateSignedEnvelope(peer, recordAddresses, 5);
        store.Discover(record);
        store.Discover([Address(peer, 2)], isTrusted: false);
        store.Discover([Address(peer, 3)], isTrusted: false);
        store.Discover(Record(peer, 4));

        PeerStore.PeerInfo info = store.GetPeerInfo(peer.PeerId);
        Assert.That(info.Addrs, Is.EquivalentTo(new[] { Address(peer, 3) }));
        Assert.That(info.SignedPeerRecord, Is.EqualTo(record), "Discovery must retain the original envelope and replay watermark.");
        Assert.That(info.Seq, Is.EqualTo(5UL));
        Assert.That(notifications, Has.Count.EqualTo(2));
    }

    [Test]
    public void AppliedSignedAddressesRemainProtectedAfterAnEmptyRecord()
    {
        Identity peer = TestPeers.Identity(1);
        PeerStore store = new();
        store.Discover(Record(peer, 1));
        ByteString empty = SigningHelper.CreateSignedEnvelope(peer, [], 2);
        store.Discover(empty);
        store.Discover([Address(peer, 3)], isTrusted: false);
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(empty));
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, 1) }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConfiguredAddressesConvergeRegardlessOfRecordSourceOrder(bool peerExchangeFirst)
    {
        Identity peer = TestPeers.Identity(1);
        PeerStore store = new();
        store.Discover([Multiaddress.Decode($"/p2p/{peer.PeerId}")]);
        List<Multiaddress[]> notifications = [];
        store.OnNewPeer += notifications.Add;
        notifications.Clear();
        State state = new() { RemoteAddress = TestPeers.Multiaddr(peer), RemotePublicKey = peer.PublicKey };
        ByteString record = Record(peer, 2);
        store.Discover(record, peerExchangeFirst ? null : state);
        store.Discover(record, peerExchangeFirst ? state : null);
        store.Discover(record, state);

        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, 2) }));
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(record));
        Assert.That(store.GetPeerInfo(peer.PeerId).Seq, Is.EqualTo(2UL));
        Assert.That(state.VerifiedPeerRecordSequence, Is.EqualTo(2UL));
        Assert.That(notifications, Has.Count.EqualTo(1));
    }

    [TestCase(0UL)]
    [TestCase(2UL)]
    public void SessionAppliesOnlyTheMatchingDeferredAddresses(ulong seq)
    {
        Identity peer = TestPeers.Identity(1);
        PeerStore store = new();
        Multiaddress configured = TestPeers.Multiaddr(peer);
        store.Discover([configured]);
        State state = new() { RemoteAddress = configured, RemotePublicKey = peer.PublicKey };
        ByteString record = Record(peer, seq);
        store.Discover(record);
        store.Discover(Record(peer, seq, port: 9999), state);
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { configured }), "An equal sequence with different addresses must not replace the retained record.");
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(record));
        SignedEnvelope sessionRecord = SignedEnvelope.Parser.ParseFrom(record);
        sessionRecord.PublicKey = ByteString.CopyFrom([.. sessionRecord.PublicKey.ToByteArray(), 0x18, 0x01]);
        store.Discover(sessionRecord.ToByteString(), state);
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, seq) }));
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(record));

        ByteString newer = Record(peer, seq + 1);
        store.Discover(newer);
        store.Discover(record, state);
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, seq) }), "An older session record cannot apply superseded deferred addresses.");
        store.Discover(newer, state);
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, seq + 1) }));
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(newer));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SequenceOrderingAppliesAcrossSources(bool firstAuthenticated, bool secondAuthenticated)
    {
        Identity peer = TestPeers.Identity(1);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(peer), RemotePublicKey = peer.PublicKey };
        PeerStore store = new();
        List<Multiaddress[]> notifications = [];
        store.OnNewPeer += notifications.Add;
        ByteString newest = Record(peer, 2);
        store.Discover(newest, firstAuthenticated ? state : null);
        store.Discover(Record(peer, 1), secondAuthenticated ? state : null);
        store.Discover(Record(peer, 2, port: 9999), secondAuthenticated ? state : null);
        Assert.That(store.GetPeerInfo(peer.PeerId).Seq, Is.EqualTo(2UL));
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(newest));
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, 2) }));
        Assert.That(notifications, Has.Count.EqualTo(1));
    }

    [Test]
    public void EmptyAndZeroSequenceRecordsRetainTheirReplayWatermark()
    {
        Identity peer = TestPeers.Identity(1);
        PeerStore store = new();
        ByteString zero = SigningHelper.CreateSignedEnvelope(peer, [], 0);
        store.Discover(zero);
        Assert.That(store.GetVerifiedPeerRecordSequence(peer.PeerId), Is.EqualTo(0UL));
        store.Discover(Record(peer, 0));
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.Null);
        store.Discover(Record(peer, 1));
        ByteString empty = SigningHelper.CreateSignedEnvelope(peer, [], 2);
        store.Discover(empty);
        store.Discover(Record(peer, 1, port: 9999));
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(empty));
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, 1) }), "Empty records preserve the existing address policy.");
    }

    [Test]
    public void ConcurrentSourcesConvergeOnHighestSequence()
    {
        Identity peer = TestPeers.Identity(1);
        State state = new() { RemoteAddress = TestPeers.Multiaddr(peer), RemotePublicKey = peer.PublicKey };
        PeerStore store = new();
        ByteString[] records = Enumerable.Range(0, 20).Select(i => Record(peer, (ulong)i)).ToArray();
        Parallel.For(0, records.Length, i => store.Discover(records[i], i % 2 == 0 ? state : null));
        Assert.That(store.GetPeerInfo(peer.PeerId).Seq, Is.EqualTo(19UL));
        Assert.That(store.GetPeerInfo(peer.PeerId).SignedPeerRecord, Is.EqualTo(records[19]));
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { Address(peer, 19) }));
    }

    [Test]
    public void StatelessSignedRecordsDoNotAcquireEvictionProtection()
    {
        PeerStore store = new(2);
        store.Discover([TestPeers.Multiaddr(1)]);
        Identity second = TestPeers.Identity(2);
        Identity third = TestPeers.Identity(3);
        store.Discover(Record(second, 1));
        store.Discover(Record(third, 1));
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(1), out _), Is.True);
        Assert.That(store.TryGetPeerInfo(second.PeerId, out _), Is.False);
        Assert.That(store.TryGetPeerInfo(third.PeerId, out var info), Is.True);
        Assert.That(info!.SignedPeerRecord, Is.Not.Null);

        PeerStore protectedStore = new(1);
        protectedStore.Discover([TestPeers.Multiaddr(1)]);
        Assert.DoesNotThrow(() => protectedStore.Discover(Record(second, 1)));
        Assert.That(protectedStore.TryGetPeerInfo(second.PeerId, out _), Is.False);

        PeerStore activeStore = new(1);
        activeStore.Retain(TestPeers.PeerId(1));
        try
        {
            Assert.DoesNotThrow(() => activeStore.Discover(Record(second, 1)));
            Assert.That(activeStore.TryGetPeerInfo(second.PeerId, out _), Is.False);
            Assert.That(activeStore.TryGetPeerInfo(TestPeers.PeerId(1), out _), Is.True);
        }
        finally
        {
            activeStore.Release(TestPeers.PeerId(1));
        }
    }

    [Test]
    public void StatelessRecordPreservesConfiguredAddressesAndPublishesOutsideLock()
    {
        Identity peer = TestPeers.Identity(1);
        PeerStore store = new();
        Multiaddress configured = TestPeers.Multiaddr(peer);
        store.Discover([configured]);
        int notifications = 0;
        store.OnNewPeer += _ => notifications++;
        notifications = 0;
        store.Discover(Record(peer, 2));
        Assert.That(store.GetPeerInfo(peer.PeerId).Seq, Is.EqualTo(2UL));
        Assert.That(store.GetPeerInfo(peer.PeerId).Addrs, Is.EquivalentTo(new[] { configured }));
        Assert.That(notifications, Is.Zero);

        PeerStore untrusted = new();
        Lock storeLock = (Lock)typeof(PeerStore).GetField("_storeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(untrusted)!;
        untrusted.OnNewPeer += _ => Assert.That(Task.Run(() =>
        {
            if (!storeLock.TryEnter()) return false;
            storeLock.Exit();
            return true;
        }).GetAwaiter().GetResult(), Is.True);
        untrusted.Discover(Record(peer, 2));
    }

    private static Multiaddress Address(Identity peer, ulong seq, int? port = null) => $"/ip4/192.0.2.1/tcp/{port ?? (int)seq + 4000}/p2p/{peer.PeerId}";
    private static ByteString Record(Identity peer, ulong seq, int? port = null) => SigningHelper.CreateSignedEnvelope(peer, [Address(peer, seq, port)], seq);
}
