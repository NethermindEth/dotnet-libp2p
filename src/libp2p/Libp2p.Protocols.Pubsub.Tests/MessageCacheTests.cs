// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class MessageCacheTests
{
    [Test]
    public void GossipIds_UseOnlyTheMostRecentWindows()
    {
        MessageCache cache = new(gossipWindows: 2, historyWindows: 3, maxEntries: 10, maxBytes: 1024);
        MessageId firstId = new([1]);
        MessageId secondId = new([2]);
        MessageId thirdId = new([3]);

        cache.Put(firstId, CreateMessage("topic", [1]));
        cache.Shift();
        cache.Put(secondId, CreateMessage("topic", [2]));
        cache.Shift();
        cache.Put(thirdId, CreateMessage("topic", [3]));

        Assert.That(cache.GetGossipIds("topic"), Is.EquivalentTo(new[] { secondId, thirdId }));
    }

    [Test]
    public void Shift_DropsTheOldestHistoryWindow()
    {
        MessageCache cache = new(gossipWindows: 2, historyWindows: 3, maxEntries: 10, maxBytes: 1024);
        MessageId firstId = new([1]);
        MessageId secondId = new([2]);
        MessageId thirdId = new([3]);

        cache.Put(firstId, CreateMessage("topic", [1]));
        cache.Shift();
        cache.Put(secondId, CreateMessage("topic", [2]));
        cache.Shift();
        cache.Put(thirdId, CreateMessage("topic", [3]));
        cache.Shift();

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryGet(firstId, out _), Is.False);
            Assert.That(cache.TryGet(secondId, out _), Is.True);
            Assert.That(cache.TryGet(thirdId, out _), Is.True);
        });
    }

    [Test]
    public void Put_EvictsTheOldestEntryWhenTheEntryLimitIsReached()
    {
        MessageCache cache = new(gossipWindows: 2, historyWindows: 3, maxEntries: 2, maxBytes: 1024);
        MessageId firstId = new([1]);
        MessageId secondId = new([2]);
        MessageId thirdId = new([3]);

        cache.Put(firstId, CreateMessage("topic", [1]));
        cache.Put(secondId, CreateMessage("topic", [2]));
        cache.Put(thirdId, CreateMessage("topic", [3]));

        Assert.Multiple(() =>
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.HistoryEntryCount, Is.EqualTo(2));
            Assert.That(cache.TryGet(firstId, out _), Is.False);
            Assert.That(cache.GetGossipIds("topic"), Does.Not.Contain(firstId));
        });
    }

    [Test]
    public void Put_EvictsTheOldestEntryWhenTheByteLimitIsReached()
    {
        Message first = CreateMessage("topic", new byte[128]);
        Message second = CreateMessage("topic", new byte[128]);
        MessageCache cache = new(gossipWindows: 2, historyWindows: 3, maxEntries: 10, maxBytes: first.CalculateSize() + second.CalculateSize() - 1);
        MessageId firstId = new([1]);
        MessageId secondId = new([2]);

        cache.Put(firstId, first);
        cache.Put(secondId, second);

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryGet(firstId, out _), Is.False);
            Assert.That(cache.TryGet(secondId, out _), Is.True);
            Assert.That(cache.CachedBytes, Is.EqualTo(second.CalculateSize()));
        });
    }

    [Test]
    public async Task PublishedMessages_AreAvailableForIwantResponses()
    {
        const string topic = "topic";
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore, new PubsubSettings { HeartbeatInterval = int.MaxValue });
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(1));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        using CancellationTokenSource cancellation = new();
        await router.StartAsync(localPeer, cancellation.Token);

        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        PeerId remotePeerId = remoteAddress.GetPeerId()!;
        List<Rpc> sentRpcs = [];
        TaskCompletionSource connection = new();
        _ = router.GetTopic(topic);
        router.OutboundConnection(remoteAddress, PubsubRouter.GossipsubProtocolVersionV11, connection.Task, sentRpcs.Add);
        router.OnRpc(remotePeerId, new Rpc().WithTopics([topic], []));
        sentRpcs.Clear();

        router.Publish(topic, [1, 2, 3]);
        Message published = sentRpcs.Single().Publish.Single();
        MessageId messageId = PubsubSettings.ConcatFromAndSeqno(published);
        sentRpcs.Clear();

        Rpc request = new() { Control = new ControlMessage() };
        request.Control.Iwant.Add(new ControlIWant { MessageIDs = { ByteString.CopyFrom(messageId.Bytes) } });
        router.OnRpc(remotePeerId, request);

        Assert.That(sentRpcs.Single().Publish.Single().Data.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        connection.SetResult();
        cancellation.Cancel();
    }

    [TestCase(0, 1, 1, 1)]
    [TestCase(2, 1, 1, 1)]
    [TestCase(1, 1, 1, 1)]
    [TestCase(1, 1, 0, 1)]
    [TestCase(1, 1, 1, 0)]
    public void Constructor_RejectsInvalidLimits(int gossipWindows, int historyWindows, int maxEntries, int maxBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageCache(gossipWindows, historyWindows, maxEntries, maxBytes));
    }

    private static Message CreateMessage(string topic, byte[] data) => new()
    {
        Topic = topic,
        Data = ByteString.CopyFrom(data),
    };
}
