// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Floodsub;

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

    public HashSet<PeerId> GraftingPeers { get; set; }
    public DateTime LastPublished { get; set; }

    public event Action<byte[]>? OnMessage;

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }
}
