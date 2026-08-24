// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
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
            new Message { Signature = ByteString.Empty },
            new Message { From = ByteString.Empty },
            new Message { Seqno = ByteString.Empty },
            new Message { Key = ByteString.Empty },
        ];

        Assert.Multiple(() =>
        {
            foreach (Message message in messages)
            {
                Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictNoSign), Is.False);
            }
        });
    }
}
