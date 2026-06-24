// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public interface ITopic
{
    event Action<PeerId, byte[]>? OnMessage;

    void Publish(byte[] bytes);
    void Publish(IMessage msg) => Publish(msg.ToByteArray());

    bool IsSubscribed { get; }
    void Subscribe();
    void Unsubscribe();
}
