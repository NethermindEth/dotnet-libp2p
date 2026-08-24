// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
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
