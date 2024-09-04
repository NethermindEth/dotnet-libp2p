// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

public interface ITopicSubscription
{
    event Action<byte[]>? OnMessage;
    void Publish(byte[] bytes);
    void Unsubscribe();
}
