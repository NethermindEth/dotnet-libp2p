// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class StrictNoSignTests
{
    [Test]
    public void StrictNoSign_AcceptsMessagesWithoutAuthorshipFields()
    {
        Message message = new()
        {
            Topic = "no-sign",
            Data = ByteString.CopyFrom([1, 2, 3]),
        };

        Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictNoSign), Is.True);
    }

    [Test]
    public void StrictNoSign_RejectsExplicitlyPresentAuthorshipFields()
    {
        Message[] messages =
        [
            new Message { Topic = "no-sign", Signature = ByteString.Empty },
            new Message { Topic = "no-sign", From = ByteString.Empty },
            new Message { Topic = "no-sign", Seqno = ByteString.Empty },
            new Message { Topic = "no-sign", Key = ByteString.Empty },
        ];

        Assert.Multiple(() =>
        {
            foreach (Message message in messages)
            {
                Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictNoSign), Is.False);
            }
        });
    }

    [Test]
    public void StrictNoSign_RequiresACustomMessageId()
    {
        PubsubSettings settings = new()
        {
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        };

        InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => new PubsubRouter(new PeerStore(), settings));

        Assert.That(exception!.Message, Does.Contain("GetMessageId"));
    }

    [Test]
    public void StrictNoSign_DeliversDistinctMessagesWithACustomMessageId()
    {
        PubsubSettings settings = new()
        {
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
            GetMessageId = message => new(message.Data.ToByteArray()),
        };
        using PubsubRouter router = new(new PeerStore(), settings);
        ITopic topic = router.GetTopic("no-sign");
        int deliveries = 0;
        topic.OnMessage += (_, _) => deliveries++;

        router.OnRpc(TestPeers.PeerId(1), CreateUnsignedMessage("first"));
        router.OnRpc(TestPeers.PeerId(1), CreateUnsignedMessage("second"));

        Assert.That(deliveries, Is.EqualTo(2));
    }

    [TestCase(PubsubSettings.SignaturePolicy.StrictNoSign, PubsubRouter.FloodsubProtocolVersion)]
    [TestCase(PubsubSettings.SignaturePolicy.StrictNoSign, PubsubRouter.GossipsubProtocolVersionV11)]
    [TestCase(PubsubSettings.SignaturePolicy.StrictSign, PubsubRouter.FloodsubProtocolVersion)]
    [TestCase(PubsubSettings.SignaturePolicy.StrictSign, PubsubRouter.GossipsubProtocolVersionV11)]
    public async Task Publish_RespectsSignaturePolicy(PubsubSettings.SignaturePolicy policy, string protocol)
    {
        const string topicName = "publish-policy";
        PubsubSettings settings = new()
        {
            DefaultSignaturePolicy = policy,
            GetMessageId = message => new(message.Data.ToByteArray()),
        };
        using PubsubRouter sender = new(new PeerStore(), settings);
        using PubsubRouter receiver = new(new PeerStore(), settings);
        LocalPeerStub localPeer = new();
        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        await sender.StartAsync(localPeer, stopped.Token);

        List<byte[]> deliveries = [];
        receiver.GetTopic(topicName).OnMessage += (_, payload) => deliveries.Add(payload);
        Multiaddress receiverAddress = TestPeers.Multiaddr(1);
        List<Rpc> sent = [];
        TaskCompletionSource connectionClosed = new();
        sender.OutboundConnection(receiverAddress, protocol, connectionClosed.Task, sent.Add);
        sender.OnRpc(receiverAddress.GetPeerId()!, new Rpc().WithTopics([topicName], []));

        byte[][] payloads = [[1, 2, 3], [4, 5, 6]];
        foreach (byte[] payload in payloads)
        {
            sender.Publish(topicName, payload);
        }

        Rpc[] published = sent.Where(rpc => rpc.Publish.Count > 0)
            .Select(rpc => Rpc.Parser.ParseFrom(rpc.ToByteArray())).ToArray();
        Assert.That(published, Has.Length.EqualTo(payloads.Length));
        for (int i = 0; i < published.Length; i++)
        {
            Message message = published[i].Publish.Single();
            bool signed = policy == PubsubSettings.SignaturePolicy.StrictSign;
            Assert.Multiple(() =>
            {
                Assert.That(message.Topic, Is.EqualTo(topicName));
                Assert.That(message.Data.ToByteArray(), Is.EqualTo(payloads[i]));
                Assert.That(message.HasFrom, Is.EqualTo(signed));
                Assert.That(message.HasSeqno, Is.EqualTo(signed));
                Assert.That(message.HasSignature, Is.EqualTo(signed));
                Assert.That(message.HasKey, Is.False);
                Assert.That(message.VerifySignature(policy), Is.True);
            });
            receiver.OnRpc(localPeer.Identity.PeerId, published[i]);
        }

        Assert.That(deliveries, Is.EqualTo(payloads));
        connectionClosed.SetResult();
    }

    private static Rpc CreateUnsignedMessage(string payload)
    {
        Rpc rpc = new();
        rpc.Publish.Add(new Message
        {
            Topic = "no-sign",
            Data = ByteString.CopyFromUtf8(payload),
        });
        return rpc;
    }
}
