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
        if (!IsSubscribed || this.topicName != topicName)
        {
            return;
        }

        // User callbacks must not hold the routing lock: they may wait for work
        // on another thread that needs the router. An in-flight delivery may finish
        // concurrently with unsubscribe.
        Action<PeerId, byte[]>? onMessage = OnMessage;
        onMessage?.Invoke(peerId, message);
    }

    public DateTime LastPublished { get; set; }

    private volatile bool isSubscribed;
    public bool IsSubscribed { get => isSubscribed; internal set => isSubscribed = value; }

    public event Action<PeerId, byte[]>? OnMessage;

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }

    public void Unsubscribe()
    {
        router.Unsubscribe(topicName);
    }

    public void Subscribe()
    {
        router.Subscribe(topicName);
    }
}
