// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter
{
    private readonly ConcurrentDictionary<string, Topic> topicState = new();

    private bool IsLocallySubscribed(string topic) => topicState.TryGetValue(topic, out Topic? state) && state.IsSubscribed;

    public ITopic GetTopic(string topicId, bool subscribe = true)
    {
        Topic topic = topicState.GetOrAdd(topicId, static (id, router) => new(router, id), this);

        if (subscribe)
        {
            Subscribe(topicId);
        }

        return topic;
    }

    /// <summary>
    /// Gets a topic configured for the opt-in Gossipsub v1.3 Partial Messages extension.
    /// Unsubscribe before disabling partial-message requests on a subscribed topic.
    /// </summary>
    public IPartialMessagesTopic GetPartialMessagesTopic(string topicId, PartialMessagesTopicOptions options, bool subscribe = true)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        ArgumentNullException.ThrowIfNull(options);
        if (!_settings.EnablePartialMessages)
        {
            throw new InvalidOperationException("Partial messages are not enabled. Set EnablePartialMessages before creating a partial messages topic.");
        }

        lock (this)
        {
            Topic topic = topicState.GetOrAdd(topicId, static (id, router) => new(router, id), this);
            bool wasSubscribed = topic.IsSubscribed;
            IPartialMessagesTopic partialMessagesTopic = topic.ConfigurePartialMessages(options);

            if (subscribe)
            {
                Subscribe(topicId);
            }

            if (wasSubscribed)
            {
                AnnounceSubscription(topicId);
            }

            return partialMessagesTopic;
        }
    }

    private Rpc.Types.SubOpts CreateSubscription(string topicId, bool subscribe)
    {
        Rpc.Types.SubOpts subscription = new() { Subscribe = subscribe, Topicid = topicId };
        if (subscribe && _settings.EnablePartialMessages && topicState.TryGetValue(topicId, out Topic? topic))
        {
            if (topic.RequestsPartialMessages)
            {
                subscription.RequestsPartial = true;
            }

            if (topic.SupportsSendingPartialMessages)
            {
                subscription.SupportsSendingPartial = true;
            }
        }

        return subscription;
    }

    private void AnnounceSubscription(string topicId)
    {
        lock (this)
        {
            if (topicState.GetValueOrDefault(topicId)?.IsSubscribed is not true)
            {
                return;
            }

            Rpc topicUpdate = new();
            topicUpdate.Subscriptions.Add(CreateSubscription(topicId, subscribe: true));
            foreach (KeyValuePair<PeerId, PubsubPeer> peer in peerState)
            {
                peer.Value.Send(topicUpdate);
            }
        }
    }

    public void Subscribe(string topicId)
    {
        lock (this)
        {
            Topic topic = topicState.GetOrAdd(topicId, static (id, router) => new Topic(router, id), this);
            if (topic.IsSubscribed)
            {
                return;
            }

            topic.IsSubscribed = true;
            UpdateRemoteSubscriptionPriority(topicId);

            HashSet<PeerId> meshPeers = mesh.GetOrAdd(topicId, []);
            HashSet<PeerId> promotedPeers = [];

            if (fanout.TryRemove(topicId, out HashSet<PeerId>? fanoutPeers))
            {
                foreach (PeerId peerId in fanoutPeers)
                {
                    if (!IsDirectPeer(peerId) &&
                        gPeers.TryGetValue(topicId, out HashSet<PeerId>? remotePeers) && remotePeers.Contains(peerId) &&
                        peerState.TryGetValue(peerId, out PubsubPeer? peer) &&
                        ScoreOf(peer) >= 0 &&
                        !IsInBackoff(peerId, topicId) &&
                        meshPeers.Add(peerId))
                    {
                        RecordPeerJoinMesh(peerId, topicId);
                        promotedPeers.Add(peerId);
                    }
                }

                fanoutLastPublished.TryRemove(topicId, out _);
            }

            foreach (PubsubPeer peer in peerState.Values)
            {
                Rpc topicUpdate = new();
                topicUpdate.Subscriptions.Add(CreateSubscription(topicId, subscribe: true));
                if (promotedPeers.Contains(peer.PeerId))
                {
                    topicUpdate.Ensure(r => r.Control.Graft).Add(new ControlGraft { TopicID = topicId });
                }
                peer.Send(topicUpdate);
            }
        }
    }

    public void Unsubscribe(string topicId)
    {
        lock (this)
        {
            UnsubscribeTopics([topicId]);
        }
    }

    // Called under the router lock so state changes and notifications stay ordered.
    private void UnsubscribeTopics(IEnumerable<string> topicIds)
    {
        Dictionary<PeerId, Rpc> peerMessages = [];
        foreach (string topicId in topicIds)
        {
            if (!topicState.TryGetValue(topicId, out Topic? topic) || !topic.IsSubscribed)
            {
                continue;
            }

            topic.IsSubscribed = false;
            UpdateRemoteSubscriptionPriority(topicId);
            foreach (PendingValidation pending in _pendingValidations.Values.Where(p => p.Snapshot.Topic == topicId).ToArray())
            {
                RemovePendingValidation(pending);
            }

            if (mesh.TryRemove(topicId, out HashSet<PeerId>? removedMesh))
            {
                foreach (PeerId peerId in removedMesh)
                {
                    RecordPeerLeaveMesh(peerId, topicId);
                    RefreshBackoff(peerId, topicId, LocalBackoff(_settings.UnsubscribeBackoff));
                }
            }

            fanout.TryRemove(topicId, out _);
            fanoutLastPublished.TryRemove(topicId, out _);
            foreach (PeerId peerId in peerState.Keys)
            {
                if (!peerMessages.TryGetValue(peerId, out Rpc? msg))
                {
                    peerMessages[peerId] = msg = new Rpc();
                }
                msg.WithTopics([], [topicId]);
                if (removedMesh?.Contains(peerId) is true)
                {
                    msg.Ensure(r => r.Control.Prune).Add(CreatePrune(peerId, topicId, _settings.UnsubscribeBackoff));
                }
            }
        }

        foreach ((PeerId peerId, Rpc msg) in peerMessages)
        {
            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }
    }

    public void UnsubscribeAll()
    {
        lock (this)
        {
            UnsubscribeTopics(topicState.Keys);
        }
    }

    public void Publish(string topicId, byte[] message)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        ArgumentNullException.ThrowIfNull(message);

        if (localPeer is null)
        {
            throw new InvalidOperationException("Router has not been started. Call StartAsync() first.");
        }

        // Build, sign and identify the message before taking the router lock. Signing and
        // GetMessageId are both host-supplied work, and holding the lock across them stalls every
        // peer and the heartbeat. Only the sequence number needs router state, and it is unique
        // without the lock.
        Rpc rpc = new();
        if (_settings.DefaultSignaturePolicy is PubsubSettings.SignaturePolicy.StrictNoSign)
        {
            rpc.Publish.Add(new Message
            {
                Topic = topicId,
                Data = ByteString.CopyFrom(message),
            });
        }
        else
        {
            rpc.WithMessages(topicId, Interlocked.Increment(ref seqNo) - 1, localPeer.Identity.PeerId.Bytes, message, localPeer.Identity);
        }

        Message publishedMessage = rpc.Publish[0];
        SharedFrame frame = new(rpc);
        MessageId messageId;
        _messageIdCalculations.Wait();
        try
        {
            messageId = _settings.GetMessageId(publishedMessage);
        }
        finally
        {
            _messageIdCalculations.Release();
        }

        lock (this)
        {
            topicState.GetOrAdd(topicId, static (id, router) => new Topic(router, id), this);
            _seenMessages.Add(messageId);
            _messageCache.Put(messageId, publishedMessage);

            if (message.Length >= _settings.IdontwantMessageThreshold && mesh.ContainsKey(topicId))
            {
                Dictionary<PeerId, Rpc> idontwantMessages = [];
                AddIdontwantMessages(publishedMessage, messageId, source: null, idontwantMessages);
                foreach ((PeerId recipient, Rpc idontwant) in idontwantMessages)
                {
                    peerState.GetValueOrDefault(recipient)?.Send(idontwant);
                }
            }

            HashSet<PeerId> directRecipients = GetDirectPeersForTopic(topicId).ToHashSet();
            foreach (PeerId peerId in directRecipients)
            {
                if (ShouldSendFullMessage(peerId, topicId, messageId))
                {
                    peerState.GetValueOrDefault(peerId)?.SendShared(frame);
                }
            }

            // Floodsub peers always get the message.
            foreach (PeerId peerId in fPeers.GetValueOrDefault(topicId) ?? [])
            {
                if (!directRecipients.Contains(peerId) && ShouldSendFullMessage(peerId, topicId, messageId))
                {
                    peerState.GetValueOrDefault(peerId)?.SendShared(frame);
                }
            }

            // Gossipsub v1.1: Flood publishing
            if (_settings.FloodPublish && gPeers.TryGetValue(topicId, out HashSet<PeerId>? allGossipsubPeers))
            {
                // Send to all gossipsub peers above publish threshold
                foreach (PeerId peerId in allGossipsubPeers)
                {
                    if (!directRecipients.Contains(peerId) &&
                        GetPeerScore(peerId) >= _settings.PublishThreshold &&
                        ShouldSendFullMessage(peerId, topicId, messageId))
                    {
                        peerState.GetValueOrDefault(peerId)?.SendShared(frame);
                    }
                }
            }
            else if (mesh.TryGetValue(topicId, out HashSet<PeerId>? meshPeers))
            {
                // Standard gossipsub v1.0 behavior: send to mesh or fanout
                foreach (PeerId peerId in meshPeers)
                {
                    if (!directRecipients.Contains(peerId) &&
                        GetPeerScore(peerId) >= _settings.PublishThreshold &&
                        ShouldSendFullMessage(peerId, topicId, messageId))
                    {
                        peerState.GetValueOrDefault(peerId)?.SendShared(frame);
                    }
                }
            }
            else
            {
                Func<PeerId, bool>? acceptsFullMessage = CanPublishPartial(topicId)
                    ? peerId => !PeerRequestsPartial(peerId, topicId)
                    : null;
                foreach (PeerId peerId in SelectFanoutRecipients(topicId, acceptsFullMessage))
                {
                    if (!directRecipients.Contains(peerId) &&
                        MeetsPublishThreshold(peerId) &&
                        ShouldSendFullMessage(peerId, topicId, messageId))
                    {
                        peerState.GetValueOrDefault(peerId)?.SendShared(frame);
                    }
                }
            }
        }
    }

    // Called under the router lock. Compatible fanout members are preferred, with extra
    // recipients sampled for this payload without evicting peers useful for other payloads.
    private IEnumerable<PeerId> SelectFanoutRecipients(string topicId, Func<PeerId, bool>? acceptsPayload)
    {
        fanoutLastPublished[topicId] = DateTime.Now;
        HashSet<PeerId> topicFanout = fanout.GetOrAdd(topicId, _ => []);
        if (acceptsPayload is null && topicFanout.Count != 0)
        {
            return topicFanout;
        }

        PeerId[] preferred = acceptsPayload is null ? [] : topicFanout
            .Where(peerId => MeetsPublishThreshold(peerId) && acceptsPayload(peerId)).ToArray();
        int remaining = _settings.Degree - preferred.Length;
        if (remaining <= 0 || !gPeers.TryGetValue(topicId, out HashSet<PeerId>? topicPeers))
        {
            return preferred;
        }

        // Keep the nonnegative score policy for seeding a new fanout.
        double minimumScore = topicFanout.Count == 0 ? Math.Max(0, _settings.PublishThreshold) : _settings.PublishThreshold;
        PeerId[] additional = Sample(topicPeers.Where(peerId => !topicFanout.Contains(peerId) &&
            !IsDirectPeer(peerId) && GetPeerScore(peerId) >= minimumScore &&
            (acceptsPayload is null || acceptsPayload(peerId))), remaining, _random);
        topicFanout.UnionWith(additional.Take(_settings.Degree - topicFanout.Count));
        return preferred.Length == 0 ? additional : preferred.Concat(additional);
    }

    private bool MeetsPublishThreshold(PeerId peerId) => GetPeerScore(peerId) >= _settings.PublishThreshold;

    /// <summary>
    /// Sends application-defined partial data to partial-message-capable mesh or fanout peers.
    /// </summary>
    public void PublishPartial(string topicId, byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null)
    {
        EnsurePartialMessagesEnabled(topicId);
        ValidatePartialMessage(groupId, partialMessage, partsMetadata);

        if (localPeer is null)
        {
            throw new InvalidOperationException("Router has not been started. Call StartAsync() first.");
        }

        PeerId[] recipients;
        lock (this)
        {
            // Only the application knows group IDs; cached full messages retain ordinary IHAVE gossip.
            partialMessageGossip.Track(topicId, groupId);

            if (mesh.TryGetValue(topicId, out HashSet<PeerId>? meshPeers))
            {
                recipients = meshPeers.Where(peerId => GetPeerScore(peerId) >= _settings.PublishThreshold).ToArray();
            }
            else
            {
                recipients = SelectFanoutRecipients(topicId, peerId =>
                    CanReceivePartial(peerId, topicId, partialMessage is not null, partsMetadata is not null)).ToArray();
            }
        }

        foreach (PeerId peerId in recipients)
        {
            SendPartial(peerId, topicId, groupId, partialMessage, partsMetadata);
        }
    }

    /// <summary>
    /// Sends application-defined partial data to a connected peer selected by the application.
    /// </summary>
    public void SendPartial(PeerId peerId, string topicId, byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null)
    {
        EnsurePartialMessagesEnabled(topicId);
        ValidatePartialMessage(groupId, partialMessage, partsMetadata);

        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        (bool sendPartialData, bool sendPartsMetadata) = GetPartialCapabilities(peer, topicId);
        if ((!sendPartialData || partialMessage is null) && (!sendPartsMetadata || partsMetadata is null))
        {
            return;
        }

        PartialMessagesExtension partial = new()
        {
            TopicID = Google.Protobuf.ByteString.CopyFromUtf8(topicId),
            GroupID = Google.Protobuf.ByteString.CopyFrom(groupId),
        };
        if (sendPartialData && partialMessage is not null)
        {
            partial.PartialMessage = Google.Protobuf.ByteString.CopyFrom(partialMessage);
        }

        if (sendPartsMetadata && partsMetadata is not null)
        {
            partial.PartsMetadata = Google.Protobuf.ByteString.CopyFrom(partsMetadata);
        }

        peer.Send(new Rpc { Partial = partial });
    }

    private void EnsurePartialMessagesEnabled(string topicId)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        if (!CanPublishPartial(topicId))
        {
            throw new InvalidOperationException($"Partial messages are not enabled for topic '{topicId}'.");
        }
    }

    private bool CanPublishPartial(string topicId) => _settings.EnablePartialMessages &&
        topicState.TryGetValue(topicId, out Topic? topic) && topic.SupportsSendingPartialMessages;

    private static (bool Data, bool Metadata) GetPartialCapabilities(PubsubPeer peer, string topicId) =>
        peer.SupportsPartialMessagesExtension
            ? (peer.RequestsPartialMessages(topicId), peer.SupportsSendingPartialMessages(topicId))
            : (false, false);

    private bool CanReceivePartial(PeerId peerId, string topicId, bool hasData, bool hasMetadata)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer)) return false;
        (bool data, bool metadata) = GetPartialCapabilities(peer, topicId);
        return (hasData && data) || (hasMetadata && metadata);
    }

    private bool PeerRequestsPartial(PeerId peerId, string topicId) =>
        peerState.TryGetValue(peerId, out PubsubPeer? peer) && peer.SupportsPartialMessagesExtension && peer.RequestsPartialMessages(topicId);

    private bool ShouldSendFullMessage(PeerId peerId, string topicId, MessageId messageId) =>
        !IsUnwantedBy(peerId, messageId) && (!CanPublishPartial(topicId) || !PeerRequestsPartial(peerId, topicId));

    private static void ValidatePartialMessage(byte[] groupId, byte[]? partialMessage, byte[]? partsMetadata)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        if (partialMessage is null && partsMetadata is null)
        {
            throw new ArgumentException("A partial message or parts metadata must be supplied.");
        }
    }

}
