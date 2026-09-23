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

    public ITopic GetTopic(string topicId, bool subscribe = true)
    {
        Topic topic = topicState.GetOrAdd(topicId, (tId) => new(this, tId));

        if (subscribe)
        {
            Subscribe(topicId);
        }

        return topic;
    }

    /// <summary>
    /// Gets a topic configured for the opt-in Gossipsub v1.3 Partial Messages extension.
    /// </summary>
    public IPartialMessagesTopic GetPartialMessagesTopic(string topicId, PartialMessagesTopicOptions options, bool subscribe = true)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        ArgumentNullException.ThrowIfNull(options);
        if (!_settings.EnablePartialMessages)
        {
            throw new InvalidOperationException("Partial messages are not enabled. Set EnablePartialMessages before creating a partial messages topic.");
        }

        Topic topic = topicState.GetOrAdd(topicId, (tId) => new(this, tId));
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
            Topic topic = topicState.GetOrAdd(topicId, (id) => new Topic(this, topicId));
            if (topic.IsSubscribed)
            {
                return;
            }

            topic.IsSubscribed = true;

            fPeers.TryAdd(topicId, []);
            gPeers.TryAdd(topicId, []);

            HashSet<PeerId> meshPeers = mesh.GetOrAdd(topicId, []);
            HashSet<PeerId> promotedPeers = [];

            if (fanout.TryRemove(topicId, out HashSet<PeerId>? fanoutPeers))
            {
                foreach (PeerId peerId in fanoutPeers)
                {
                    if (!IsDirectPeer(peerId) &&
                        gPeers[topicId].Contains(peerId) &&
                        peerState.TryGetValue(peerId, out PubsubPeer? peer) &&
                        GetPeerScore(peerId) >= 0 &&
                        (!peer.Backoff.TryGetValue(topicId, out DateTime backoff) || backoff <= DateTime.Now) &&
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

            if (mesh.TryRemove(topicId, out HashSet<PeerId>? removedMesh))
            {
                foreach (PeerId peerId in removedMesh)
                {
                    RecordPeerLeaveMesh(peerId, topicId);
                    if (peerState.TryGetValue(peerId, out PubsubPeer? peer))
                    {
                        DateTime backoffUntil = DateTime.Now.AddMilliseconds(_settings.UnsubscribeBackoff);
                        if (!peer.Backoff.TryGetValue(topicId, out DateTime existingBackoff) || existingBackoff < backoffUntil)
                        {
                            peer.Backoff[topicId] = backoffUntil;
                        }
                    }
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
                    msg.Ensure(r => r.Control.Prune).Add(new ControlPrune
                    {
                        TopicID = topicId,
                        Backoff = (ulong)Math.Ceiling(_settings.UnsubscribeBackoff / 1000.0),
                    });
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

        lock (this)
        {
            topicState.GetOrAdd(topicId, (id) => new Topic(this, topicId));

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
                rpc.WithMessages(topicId, seqNo++, localPeer.Identity.PeerId.Bytes, message, localPeer.Identity);
            }

            HashSet<PeerId> directRecipients = GetDirectPeersForTopic(topicId).ToHashSet();
            foreach (PeerId peerId in directRecipients)
            {
                if (ShouldSendFullMessage(peerId, topicId))
                {
                    peerState.GetValueOrDefault(peerId)?.Send(rpc);
                }
            }

            // Floodsub peers always get the message.
            foreach (PeerId peerId in fPeers.GetValueOrDefault(topicId) ?? [])
            {
                if (!directRecipients.Contains(peerId) && ShouldSendFullMessage(peerId, topicId))
                {
                    peerState.GetValueOrDefault(peerId)?.Send(rpc);
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
                        ShouldSendFullMessage(peerId, topicId))
                    {
                        peerState.GetValueOrDefault(peerId)?.Send(rpc);
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
                        ShouldSendFullMessage(peerId, topicId))
                    {
                        peerState.GetValueOrDefault(peerId)?.Send(rpc);
                    }
                }
            }
            else
            {
                fanoutLastPublished[topicId] = DateTime.Now;
                HashSet<PeerId> topicFanout = fanout.GetOrAdd(topicId, _ => []);

                if (topicFanout.Count == 0)
                {
                    HashSet<PeerId>? topicPeers = gPeers.GetValueOrDefault(topicId);
                    if (topicPeers is { Count: > 0 })
                    {
                        // Select peers with non-negative scores
                        var eligiblePeers = topicPeers.Where(p => !IsDirectPeer(p) && GetPeerScore(p) >= 0).ToList();
                        foreach (PeerId peer in eligiblePeers.Take(_settings.Degree))
                        {
                            topicFanout.Add(peer);
                        }
                    }
                }

                foreach (PeerId peerId in topicFanout)
                {
                    if (!directRecipients.Contains(peerId) &&
                        GetPeerScore(peerId) >= _settings.PublishThreshold &&
                        ShouldSendFullMessage(peerId, topicId))
                    {
                        peerState.GetValueOrDefault(peerId)?.Send(rpc);
                    }
                }
            }
        }
    }

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
            partialMessageGossip.Track(topicId, groupId);

            if (mesh.TryGetValue(topicId, out HashSet<PeerId>? meshPeers))
            {
                recipients = meshPeers.Where(peerId => GetPeerScore(peerId) >= _settings.PublishThreshold).ToArray();
            }
            else
            {
                fanoutLastPublished[topicId] = DateTime.Now;
                HashSet<PeerId> fanoutPeers = fanout.GetOrAdd(topicId, _ => []);
                if (fanoutPeers.Count == 0 && gPeers.TryGetValue(topicId, out HashSet<PeerId>? topicPeers))
                {
                    foreach (PeerId peerId in topicPeers.Where(peerId => !IsDirectPeer(peerId) && GetPeerScore(peerId) >= 0).Take(_settings.Degree))
                    {
                        fanoutPeers.Add(peerId);
                    }
                }

                recipients = fanoutPeers.Where(peerId => GetPeerScore(peerId) >= _settings.PublishThreshold).ToArray();
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

        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer) || !peer.SupportsPartialMessagesExtension)
        {
            return;
        }

        bool sendPartialData = peer.RequestsPartialMessages(topicId);
        bool sendPartsMetadata = peer.SupportsSendingPartialMessages(topicId);
        if (!sendPartialData && !sendPartsMetadata)
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

        if (partial.HasPartialMessage || partial.HasPartsMetadata)
        {
            peer.Send(new Rpc { Partial = partial });
        }
    }

    private void EnsurePartialMessagesEnabled(string topicId)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        if (!_settings.EnablePartialMessages ||
            !topicState.TryGetValue(topicId, out Topic? topic) ||
            !topic.SupportsSendingPartialMessages)
        {
            throw new InvalidOperationException($"Partial messages are not enabled for topic '{topicId}'.");
        }
    }

    private bool ShouldSendFullMessage(PeerId peerId, string topicId)
    {
        return !_settings.EnablePartialMessages ||
            !topicState.TryGetValue(topicId, out Topic? topic) ||
            !topic.SupportsSendingPartialMessages ||
            !peerState.TryGetValue(peerId, out PubsubPeer? peer) ||
            !peer.SupportsPartialMessagesExtension ||
            !peer.RequestsPartialMessages(topicId);
    }

    private static void ValidatePartialMessage(byte[] groupId, byte[]? partialMessage, byte[]? partsMetadata)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        if (partialMessage is null && partsMetadata is null)
        {
            throw new ArgumentException("A partial message or parts metadata must be supplied.");
        }
    }

}
