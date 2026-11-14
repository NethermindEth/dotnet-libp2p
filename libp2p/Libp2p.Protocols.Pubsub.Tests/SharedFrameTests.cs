// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class SharedFrameTests
{
    private const string Topic = "topic";
    private const int Peers = 4;

    [Test]
    public void ForwardedMessageIsSerializedOnceForAllRecipients()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue });
        router.GetTopic(Topic);
        List<ReadOnlyMemory<byte>>[] frames = ConnectMeshPeers(router);
        Identity author = TestPeers.Identity(Peers + 1);

        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithMessages(Topic, 1, author.PeerId.Bytes, [7], author));

        AssertOneSharedFrame(frames[1..], 7);
    }

    [Test]
    public async Task PublishedMessageIsSerializedOnceForAllRecipients()
    {
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue });
        await router.StartAsync(new LocalPeerStub());
        router.GetTopic(Topic);
        List<ReadOnlyMemory<byte>>[] frames = ConnectMeshPeers(router);

        router.Publish(Topic, [7]);
        await router.FlushOutboundAsync();

        AssertOneSharedFrame(frames, 7);
    }

    [Test]
    public void RecipientsOfDifferentMessagesGetTheirOwnFrames()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue });
        router.GetTopic("t1");
        router.GetTopic("t2");
        List<ReadOnlyMemory<byte>> first = Connect(router, 2, "t1");
        List<ReadOnlyMemory<byte>> second = Connect(router, 3, "t2");
        Connect(router, 1, "t1", "t2");
        first.Clear();
        second.Clear();
        Identity author = TestPeers.Identity(9);
        Rpc rpc = new Rpc().WithMessages("t1", 1, author.PeerId.Bytes, [1], author).WithMessages("t2", 2, author.PeerId.Bytes, [2], author);

        router.OnRpc(TestPeers.PeerId(1), rpc);

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(RouterTestExtensions.Decode(first[0]).Publish.Select(m => m.Topic), Is.EqualTo(new[] { "t1" }));
            Assert.That(RouterTestExtensions.Decode(second[0]).Publish.Select(m => m.Topic), Is.EqualTo(new[] { "t2" }));
        });
    }

    [Test]
    public void EachRecipientIsChargedTheFullSharedFrame()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue });
        router.GetTopic(Topic);
        TaskCompletionSource<IOResult> blocked = new();
        for (int i = 2; i <= Peers + 1; i++)
        {
            router.OutboundConnection(TestPeers.Multiaddr(i), PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
                (_, _) => new ValueTask<IOResult>(blocked.Task), () => ValueTask.CompletedTask, () => Task.CompletedTask);
            Rpc hello = new Rpc().WithTopics([Topic], []);
            hello.Control = new ControlMessage { Graft = { new ControlGraft { TopicID = Topic } } };
            router.OnRpcAsync(TestPeers.PeerId(i), hello, PubsubRouter.GossipsubProtocolVersionV11).GetAwaiter().GetResult();
        }
        (int Entries, long Bytes) before = router.OutboundUsage;
        Identity author = TestPeers.Identity(Peers + 5);
        Rpc incoming = new Rpc().WithMessages(Topic, 1, author.PeerId.Bytes, new byte[100], author);
        int length = incoming.CalculateSize();
        int expected = length + VarInt.GetSizeInBytes((ulong)length);

        router.OnRpcAsync(TestPeers.PeerId(2), incoming, PubsubRouter.GossipsubProtocolVersionV11).GetAwaiter().GetResult();

        Assert.That(router.OutboundUsage, Is.EqualTo((before.Entries + Peers - 1, before.Bytes + (Peers - 1) * (long)expected)));
        blocked.SetResult(IOResult.Ok);
    }

    private static List<ReadOnlyMemory<byte>> Connect(PubsubRouter router, int index, params string[] topics)
    {
        List<ReadOnlyMemory<byte>> frames = [];
        router.OutboundConnection(TestPeers.Multiaddr(index), PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
            (bytes, _) =>
            {
                lock (frames) frames.Add(bytes);
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => Task.CompletedTask);
        Rpc hello = new Rpc().WithTopics(topics, []);
        hello.Control = new ControlMessage();
        foreach (string topic in topics)
        {
            hello.Control.Graft.Add(new ControlGraft { TopicID = topic });
        }
        router.OnRpc(TestPeers.PeerId(index), hello, PubsubRouter.GossipsubProtocolVersionV11);
        return frames;
    }

    private static List<ReadOnlyMemory<byte>>[] ConnectMeshPeers(PubsubRouter router)
    {
        List<ReadOnlyMemory<byte>>[] frames = new List<ReadOnlyMemory<byte>>[Peers];
        for (int i = 0; i < Peers; i++)
        {
            List<ReadOnlyMemory<byte>> peerFrames = frames[i] = [];
            router.OutboundConnection(TestPeers.Multiaddr(i + 1), PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
                (bytes, _) =>
                {
                    lock (peerFrames) peerFrames.Add(bytes);
                    return ValueTask.FromResult(IOResult.Ok);
                }, () => ValueTask.CompletedTask, () => Task.CompletedTask);
            Rpc hello = new Rpc().WithTopics([Topic], []);
            hello.Control = new ControlMessage { Graft = { new ControlGraft { TopicID = Topic } } };
            router.OnRpc(TestPeers.PeerId(i + 1), hello, PubsubRouter.GossipsubProtocolVersionV11);
        }

        foreach (List<ReadOnlyMemory<byte>> peerFrames in frames)
        {
            lock (peerFrames) peerFrames.Clear();
        }

        return frames;
    }

    private static void AssertOneSharedFrame(List<ReadOnlyMemory<byte>>[] frames, byte data)
    {
        byte[]? shared = null;
        foreach (List<ReadOnlyMemory<byte>> peerFrames in frames)
        {
            ReadOnlyMemory<byte> frame;
            lock (peerFrames)
            {
                Assert.That(peerFrames, Has.Count.EqualTo(1));
                frame = peerFrames[0];
            }

            Rpc rpc = RouterTestExtensions.Decode(frame);
            Assert.That(rpc.Publish, Has.Count.EqualTo(1));
            Assert.That(rpc.Publish[0].Data.ToByteArray(), Is.EqualTo(new[] { data }));
            Assert.That(MemoryMarshal.TryGetArray(frame, out ArraySegment<byte> segment), Is.True);
            shared ??= segment.Array;
            Assert.That(segment.Array, Is.SameAs(shared), "Every recipient must queue the same serialized bytes.");
        }
    }
}
