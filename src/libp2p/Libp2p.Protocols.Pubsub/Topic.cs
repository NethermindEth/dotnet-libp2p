// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class Topic : ITopic
{
    private readonly PubsubRouter router;
    private readonly string topicName;
    private PartialMessagesTopic? partialMessagesTopic;

    public Topic(PubsubRouter router, string topicName)
    {
        this.router = router;
        this.topicName = topicName;
        router.OnMessage += OnRouterMessage;
    }

    private void OnRouterMessage(string topicName, PeerId peerId, byte[] message)
    {
        lock (router)
        {
            if (!IsSubscribed || this.topicName != topicName)
            {
                return;
            }

            Action<PeerId, byte[]>? onMessage = OnMessage;
            onMessage?.Invoke(peerId, message);
        }
    }

    public DateTime LastPublished { get; set; }

    public bool IsSubscribed { get; internal set; }
    internal bool RequestsPartialMessages { get; private set; }
    internal bool SupportsSendingPartialMessages { get; private set; }
    internal PubsubRouter Router => router;
    internal string Name => topicName;

    public event Action<PeerId, byte[]>? OnMessage;

    internal IPartialMessagesTopic ConfigurePartialMessages(PartialMessagesTopicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestPartialMessages && !options.SupportsSendingPartialMessages)
        {
            throw new ArgumentException("Requesting partial messages requires support for sending partial messages.", nameof(options));
        }

        RequestsPartialMessages = options.RequestPartialMessages;
        SupportsSendingPartialMessages = options.SupportsSendingPartialMessages;
        return partialMessagesTopic ??= new PartialMessagesTopic(this);
    }

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }

    public void Unsubscribe()
    {
        if (IsSubscribed) router.Unsubscribe(topicName);
    }

    public void Subscribe()
    {
        if (!IsSubscribed) router.Subscribe(topicName);
    }
}

internal sealed class PartialMessagesTopic : IPartialMessagesTopic
{
    private readonly Topic topic;

    public PartialMessagesTopic(Topic topic)
    {
        this.topic = topic;
        topic.Router.OnPartialMessage += OnRouterPartialMessage;
    }

    public event Action<PeerId, byte[]>? OnMessage
    {
        add => topic.OnMessage += value;
        remove => topic.OnMessage -= value;
    }

    public event Action<PeerId, PartialMessage>? OnPartialMessage;

    public bool IsSubscribed => topic.IsSubscribed;
    public bool RequestsPartialMessages => topic.RequestsPartialMessages;
    public bool SupportsSendingPartialMessages => topic.SupportsSendingPartialMessages;

    public void Publish(byte[] value) => topic.Publish(value);

    public void PublishPartial(byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null)
    {
        topic.Router.PublishPartial(topic.Name, groupId, partialMessage, partsMetadata);
    }

    public void SendPartial(PeerId peerId, byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null)
    {
        topic.Router.SendPartial(peerId, topic.Name, groupId, partialMessage, partsMetadata);
    }

    public void Unsubscribe() => topic.Unsubscribe();

    public void Subscribe() => topic.Subscribe();

    private void OnRouterPartialMessage(string topicName, PeerId peerId, PartialMessage message)
    {
        if (topic.Name != topicName)
        {
            return;
        }

        OnPartialMessage?.Invoke(peerId, message);
    }
}
