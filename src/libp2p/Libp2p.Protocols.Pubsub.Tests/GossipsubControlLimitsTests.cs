// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class GossipsubControlLimitsTests
{
    [Test]
    public async Task Ihave_RespectsTheHeartbeatAndMessageIdLimits()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIHaveMessages = 1,
            MaxIHaveLength = 3,
        });

        Rpc ihaves = CreateIhave(setup.Topic, [1], [2]);
        ihaves.Control.Ihave.Add(new ControlIHave { TopicID = setup.Topic, MessageIDs = { ByteString.CopyFrom([3]) } });
        setup.Router.OnRpc(setup.RemotePeerId, ihaves);

        Assert.That(GetIwantIds(setup.SentRpcs), Has.Count.EqualTo(2));
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [4]));
        Assert.That(GetIwantIds(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [5]));
        Assert.That(GetIwantIds(setup.SentRpcs), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Iwant_UsesRetransmissionAndResponseByteLimits()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            GossipRetransmission = 2,
            MaxIwantResponseBytes = 200,
        });

        MessageId first = setup.Publish([1, 2, 3]);
        MessageId second = setup.Publish(new byte[64]);

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first, second));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);
    }

    [Test]
    public async Task Iwant_ResponseLimitIncludesProtobufEnvelopeOverhead()
    {
        PubsubSettings settings = new() { HeartbeatInterval = int.MaxValue };
        await using RouterSetup setup = await RouterSetup.Create(settings);
        MessageId messageId = setup.Publish([1, 2, 3]);
        settings.MaxIwantResponseBytes = setup.SentRpcs.Last().Publish.Single().CalculateSize();
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));

        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);
    }

    [Test]
    public async Task IDontWant_SuppressesResponsesUntilItsHeartbeatTtlExpires()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            IdontwantTtlHeartbeats = 1,
        });
        MessageId messageId = setup.Publish([1, 2, 3]);
        setup.SentRpcs.Clear();

        Rpc unwanted = new() { Control = new ControlMessage() };
        unwanted.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(messageId.Bytes) } });
        setup.Router.OnRpc(setup.RemotePeerId, unwanted);
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));
    }

    [Test]
    public void IwantPromises_AreBoundedAndFulfilledByTheMessageId()
    {
        IwantPromiseTracker tracker = new(maxPromises: 1);
        PeerId firstPeer = TestPeers.PeerId(1);
        PeerId secondPeer = TestPeers.PeerId(2);
        MessageId firstMessage = new([1]);
        MessageId secondMessage = new([2]);

        tracker.Add(firstPeer, [firstMessage], DateTime.UtcNow.AddSeconds(-1));
        tracker.Add(secondPeer, [secondMessage], DateTime.UtcNow.AddSeconds(-1));

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count, Is.EqualTo(1));
            Assert.That(tracker.TakeExpired(DateTime.UtcNow), Is.EquivalentTo(new Dictionary<PeerId, int> { [firstPeer] = 1 }));
            Assert.That(tracker.Count, Is.Zero);
        });

        tracker.Add(firstPeer, [firstMessage], DateTime.UtcNow.AddSeconds(1));
        tracker.Fulfill(firstMessage);
        Assert.That(tracker.Count, Is.Zero);
    }

    [TestCase(0, 1, 1, 1, 1, 1, 1, 1)]
    [TestCase(1, 0, 1, 1, 1, 1, 1, 1)]
    [TestCase(1, 1, 0, 1, 1, 1, 1, 1)]
    [TestCase(1, 1, 1, 0, 1, 1, 1, 1)]
    [TestCase(1, 1, 1, 1, 0, 1, 1, 1)]
    [TestCase(1, 1, 1, 1, 1, 0, 1, 1)]
    [TestCase(1, 1, 1, 1, 1, 1, 0, 1)]
    [TestCase(1, 1, 1, 1, 1, 1, 1, 0)]
    public void Router_RejectsInvalidControlLimits(
        int maxIhaveMessages,
        int maxIhaveLength,
        int retransmission,
        int responseBytes,
        int promises,
        int followupTime,
        int idontwantTtl,
        int maxIdontwantMessages)
    {
        PubsubSettings settings = new()
        {
            MaxIHaveMessages = maxIhaveMessages,
            MaxIHaveLength = maxIhaveLength,
            GossipRetransmission = retransmission,
            MaxIwantResponseBytes = responseBytes,
            MaxIwantPromises = promises,
            IWantFollowupTime = followupTime,
            IdontwantTtlHeartbeats = idontwantTtl,
            MaxIdontwantMessages = maxIdontwantMessages,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), settings));
    }

    private static Rpc CreateIhave(string topic, params byte[][] ids)
    {
        Rpc rpc = new() { Control = new ControlMessage() };
        ControlIHave ihave = new() { TopicID = topic };
        ihave.MessageIDs.AddRange(ids.Select(ByteString.CopyFrom));
        rpc.Control.Ihave.Add(ihave);
        return rpc;
    }

    private static Rpc CreateIwant(params MessageId[] ids)
    {
        Rpc rpc = new() { Control = new ControlMessage() };
        rpc.Control.Iwant.Add(new ControlIWant { MessageIDs = { ids.Select(id => ByteString.CopyFrom(id.Bytes)) } });
        return rpc;
    }

    private static IReadOnlyList<ByteString> GetIwantIds(IEnumerable<Rpc> rpcs) => rpcs
        .Where(rpc => rpc.Control is not null)
        .SelectMany(rpc => rpc.Control.Iwant)
        .SelectMany(iwant => iwant.MessageIDs)
        .ToArray();

    private static IReadOnlyList<Message> GetPublishedMessages(IEnumerable<Rpc> rpcs) => rpcs
        .SelectMany(rpc => rpc.Publish)
        .ToArray();

    private sealed class RouterSetup : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource connection = new();

        private RouterSetup(PubsubRouter router, string topic, PeerId remotePeerId, List<Rpc> sentRpcs)
        {
            Router = router;
            Topic = topic;
            RemotePeerId = remotePeerId;
            SentRpcs = sentRpcs;
        }

        public PubsubRouter Router { get; }
        public string Topic { get; }
        public PeerId RemotePeerId { get; }
        public List<Rpc> SentRpcs { get; }

        public static async Task<RouterSetup> Create(PubsubSettings settings)
        {
            const string topic = "topic";
            PubsubRouter router = new(new PeerStore(), settings);
            ILocalPeer localPeer = Substitute.For<ILocalPeer>();
            localPeer.Identity.Returns(TestPeers.Identity(1));
            localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());

            RouterSetup setup = new(router, topic, TestPeers.PeerId(2), []);
            await router.StartAsync(localPeer, setup.cancellation.Token);
            _ = router.GetTopic(topic);
            Multiaddress remoteAddress = TestPeers.Multiaddr(2);
            router.OutboundConnection(remoteAddress, PubsubRouter.GossipsubProtocolVersionV12, setup.connection.Task, setup.SentRpcs.Add);
            router.OnRpc(setup.RemotePeerId, new Rpc().WithTopics([topic], []));
            setup.SentRpcs.Clear();
            return setup;
        }

        public MessageId Publish(byte[] data)
        {
            Router.Publish(Topic, data);
            Message published = SentRpcs.Last().Publish.Single();
            return PubsubSettings.ConcatFromAndSeqno(published);
        }

        public ValueTask DisposeAsync()
        {
            connection.TrySetResult();
            cancellation.Cancel();
            cancellation.Dispose();
            Router.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
