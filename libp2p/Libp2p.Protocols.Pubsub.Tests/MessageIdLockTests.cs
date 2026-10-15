// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

/// <remarks>
/// <see cref="PubsubSettings.GetMessageId"/> is supplied by the host and can be expensive: Ethereum
/// beacon gossip decompresses Snappy and hashes the uncompressed payload. Running it under the
/// router lock stalls every other peer, so it runs before the lock is taken and its result is
/// carried through duplicate detection, scoring and logging instead of being recomputed.
/// </remarks>
[TestFixture]
public class MessageIdLockTests
{
    private const string Topic = "message-id";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public void AnExpensiveMessageIdDoesNotStallOtherPeers()
    {
        using ManualResetEventSlim calculating = new(false);
        using ManualResetEventSlim release = new(false);
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            if (message.Data.Span[0] == 1)
            {
                calculating.Set();
                release.Wait(Timeout);
            }

            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);

        Task slowPeer = Task.Run(() => router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1))));
        Assert.That(calculating.Wait(Timeout), Is.True, "The message id calculation never started.");

        Task otherPeer = Task.Run(() => router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics([Topic], [])));

        Assert.That(otherPeer.Wait(Timeout), Is.True,
            "A control-only RPC from another peer must not wait on an unrelated peer's message id calculation.");

        release.Set();
        Assert.That(slowPeer.Wait(Timeout), Is.True);
    }

    [Test]
    public void AReceivedMessageIsIdentifiedOnce()
    {
        int calculations = 0;
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            Interlocked.Increment(ref calculations);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Has.Count.EqualTo(1), "The message must still be delivered.");
            Assert.That(calculations, Is.EqualTo(1),
                "Duplicate detection, scoring and logging must reuse the id rather than recompute it.");
        });
    }

    [Test]
    public void AKnownMessageIsIdentifiedOncePerDelivery()
    {
        int calculations = 0;
        PubsubSettings settings = Settings();
        settings.GetMessageId = message =>
        {
            Interlocked.Increment(ref calculations);
            return new MessageId(message.Data.ToByteArray());
        };

        using PubsubRouter router = CreateRouter(settings);
        ConnectPeers(router);

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        int afterFirst = calculations;
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.That(calculations - afterFirst, Is.EqualTo(1),
            "A duplicate costs one calculation to recognise it, and nothing more.");
    }

    private static PubsubSettings Settings() => new()
    {
        DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        GetMessageId = message => new(message.Data.ToByteArray()),
    };

    private static PubsubRouter CreateRouter(PubsubSettings settings) => new(new PeerStore(), settings);

    private static void ConnectPeers(PubsubRouter router)
    {
        TaskCompletionSource closed = new();
        foreach (int index in (int[])[1, 2])
        {
            Multiaddress address = TestPeers.Multiaddr(index);
            router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion, closed.Task, _ => { });
            router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics([Topic], []));
        }
    }

    private static Message NewMessage(byte data) => new() { Topic = Topic, Data = ByteString.CopyFrom([data]) };

    private static Rpc RpcWith(Message message)
    {
        Rpc rpc = new();
        rpc.Publish.Add(message);
        return rpc;
    }
}
