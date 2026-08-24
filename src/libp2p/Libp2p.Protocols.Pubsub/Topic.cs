// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class Topic : IPartialMessagesTopic
{
    private readonly PubsubRouter router;
    private readonly string topicName;

    public Topic(PubsubRouter router, string topicName)
    {
        this.router = router;
        this.topicName = topicName;
        router.OnMessage += OnRouterMessage;
        router.OnPartialMessage += OnRouterPartialMessage;
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

    private void OnRouterPartialMessage(string topicName, PeerId peerId, PartialMessage message)
    {
        if (this.topicName != topicName)
        {
            return;
        }

        Action<PeerId, PartialMessage>? onPartialMessage = OnPartialMessage;
        onPartialMessage?.Invoke(peerId, message);
    }

    public DateTime LastPublished { get; set; }

    private volatile bool isSubscribed;
    public bool IsSubscribed { get => isSubscribed; internal set => isSubscribed = value; }
    public bool RequestsPartialMessages { get; private set; }
    public bool SupportsSendingPartialMessages { get; private set; }

    public event Action<PeerId, byte[]>? OnMessage;
    public event Action<PeerId, PartialMessage>? OnPartialMessage;

    internal void ConfigurePartialMessages(PartialMessagesTopicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestPartialMessages && !options.SupportSendingPartialMessages)
        {
            throw new ArgumentException("Requesting partial messages requires support for sending partial messages.", nameof(options));
        }

        RequestsPartialMessages = options.RequestPartialMessages;
        SupportsSendingPartialMessages = options.SupportSendingPartialMessages;
    }

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }

    public void PublishPartial(byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null)
    {
        router.PublishPartial(topicName, groupId, partialMessage, partsMetadata);
    }

    public void SendPartial(PeerId peerId, byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null)
    {
        router.SendPartial(peerId, topicName, groupId, partialMessage, partsMetadata);
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
