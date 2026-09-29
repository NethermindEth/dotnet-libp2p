// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class SignatureValidationTests
{
    [Test]
    public void MalformedSignedMessage_DoesNotDropFollowingValidMessage()
    {
        using PubsubRouter router = new(new PeerStore());
        List<byte[]> deliveries = [];
        router.GetTopic("signed").OnMessage += (_, data) => deliveries.Add(data);
        Identity author = new();
        Rpc rpc = new();
        rpc.Publish.Add(new Message
        {
            Topic = "signed",
            From = ByteString.CopyFrom([1]),
            Seqno = ByteString.CopyFrom([1]),
            Signature = ByteString.CopyFrom(new byte[64]),
            Data = ByteString.CopyFrom([1]),
        });
        rpc.WithMessages("signed", 2, author.PeerId.Bytes, [2], author);

        router.OnRpc(TestPeers.PeerId(1), rpc);

        Assert.That(deliveries, Is.EqualTo(new[] { new byte[] { 2 } }));
    }
}
