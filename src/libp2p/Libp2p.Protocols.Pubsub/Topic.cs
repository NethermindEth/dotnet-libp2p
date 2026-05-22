// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class Topic : ITopic
{
    private readonly PubsubRouter router;
    private readonly string topicName;

    public Topic(PubsubRouter router, string topicName)
    {
        this.router = router;
        this.topicName = topicName;
        router.OnMessage += OnRouterMessage;
    }

    private void OnRouterMessage(string topicName, PeerId peerId, byte[] message)
    {
        if (this.topicName != topicName)
        {
            return;
        }

        Action<PeerId, byte[]>? onMessage = OnMessage;
        onMessage?.Invoke(peerId, message);
    }

    public DateTime LastPublished { get; set; }

    public bool IsSubscribed { get; internal set; }

    public event Action<PeerId, byte[]>? OnMessage;

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }

    public void Unsubscribe()
    {
        if (!IsSubscribed) router.Unsubscribe(topicName);
    }

    public void Subscribe()
    {
        if (!IsSubscribed) router.Subscribe(topicName);
    }
}
