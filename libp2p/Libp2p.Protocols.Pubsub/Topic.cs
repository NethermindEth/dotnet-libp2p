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
    }

    // The router calls this for messages of this topic only. User callbacks must not hold
    // the routing lock: they may wait for work on another thread that needs the router.
    // An in-flight delivery may finish concurrently with unsubscribe.
    internal void Deliver(PeerId peerId, byte[] message)
    {
        Action<PeerId, byte[]>? onMessage = OnMessage;
        if (!IsSubscribed || onMessage is null)
        {
            return;
        }

        foreach (Action<PeerId, byte[]> handler in Delegate.EnumerateInvocationList(onMessage))
        {
            try
            {
                handler(peerId, message);
            }
            catch (Exception ex)
            {
                router.LogSubscriberError(ex, topicName);
            }
        }
    }

    public DateTime LastPublished { get; set; }

    private volatile bool isSubscribed;
    public bool IsSubscribed { get => isSubscribed; internal set => isSubscribed = value; }
    private volatile bool requestsPartialMessages;
    private volatile bool supportsSendingPartialMessages;
    internal bool RequestsPartialMessages => requestsPartialMessages;
    internal bool SupportsSendingPartialMessages => supportsSendingPartialMessages;
    internal bool HasRequestedPartialMessages { get; private set; }
    internal PubsubRouter Router => router;
    internal string Name => topicName;

    public event Action<PeerId, byte[]>? OnMessage;

    internal IPartialMessagesTopic ConfigurePartialMessages(PartialMessagesTopicOptions options)
    {
        if (options.RequestPartialMessages && !options.SupportsSendingPartialMessages)
        {
            throw new ArgumentException("Requesting partial messages requires support for sending partial messages.", nameof(options));
        }

        if (IsSubscribed && RequestsPartialMessages && !options.RequestPartialMessages)
        {
            throw new InvalidOperationException("Unsubscribe before disabling partial-message requests on a subscribed topic.");
        }

        supportsSendingPartialMessages = options.SupportsSendingPartialMessages;
        requestsPartialMessages = options.RequestPartialMessages;
        HasRequestedPartialMessages |= options.RequestPartialMessages;
        return partialMessagesTopic ??= new PartialMessagesTopic(this);
    }

    internal void DeliverPartial(PeerId peerId, PartialMessage message) => partialMessagesTopic?.Deliver(peerId, message);

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

internal sealed class PartialMessagesTopic : IPartialMessagesTopic
{
    private readonly Topic topic;

    public PartialMessagesTopic(Topic topic)
    {
        this.topic = topic;
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

    internal void Deliver(PeerId peerId, PartialMessage message)
    {
        Action<PeerId, PartialMessage>? onPartialMessage = OnPartialMessage;
        if (!IsSubscribed || !SupportsSendingPartialMessages || onPartialMessage is null)
        {
            return;
        }

        foreach (Action<PeerId, PartialMessage> handler in Delegate.EnumerateInvocationList(onPartialMessage))
        {
            try
            {
                handler(peerId, message);
            }
            catch (Exception ex)
            {
                topic.Router.LogSubscriberError(ex, topic.Name);
            }
        }
    }
}
