// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public interface ITopicSubscription
{
    event Action<byte[]>? OnMessage;
    void Publish(byte[] bytes);
    void Publish(IMessage msg)
    {
        Publish(msg.ToByteArray());
    }
    void Unsubscribe();
}
