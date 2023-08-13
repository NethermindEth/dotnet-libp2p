// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Libp2p.Protocols.Floodsub;

class Topic : ITopic
{
    private readonly PubsubRouter router;
    private string topicName;

    public Topic(PubsubRouter router, string topicName)
    {
        this.router = router;
        this.topicName = topicName;
        router.OnMessage += (topicName, message) =>
        {
            if (OnMessage is not null && this.topicName == topicName)
            {
                OnMessage(message);
            }
        };
    }

    public event Action<byte[]>? OnMessage;

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }
}
