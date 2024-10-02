// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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

    private void OnRouterMessage(string topicName, byte[] message)
    {
        if (OnMessage is not null && this.topicName == topicName)
        {
            OnMessage(message);
        }
    }

    public DateTime LastPublished { get; set; }

    public bool IsSubscribed { get; internal set; }

    public event Action<byte[]>? OnMessage;

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
