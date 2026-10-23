// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter
{
    // All membership indexes and counters change together under the router lock.
    private int _remoteSubscriptionCount;
    private long _remoteSubscriptionTopicBytes;
    private readonly HashSet<string> _remoteSubscriptionTopics = new(StringComparer.Ordinal);
    private readonly Dictionary<(PeerId Peer, string Topic), LinkedListNode<(PubsubPeer Peer, string Topic)>> _unwantedRemoteSubscriptions = [];
    private readonly LinkedList<(PubsubPeer Peer, string Topic)> _unwantedRemoteSubscriptionOrder = [];
    private long _unwantedRemoteSubscriptionTopicBytes;

    internal (int Count, long TopicBytes) RemoteSubscriptionUsage
    {
        get
        {
            lock (this)
            {
                return (_remoteSubscriptionCount, _remoteSubscriptionTopicBytes);
            }
        }
    }

    private bool TryAddRemoteSubscription(PubsubPeer peer, string topic, out string retainedTopic)
    {
        if (peer.RemoteSubscriptions.TryGetValue(topic, out string? existing))
        {
            retainedTopic = existing;
            return true;
        }

        retainedTopic = topic;
        long bytes = 2L * topic.Length;
        if (peer.RemoteSubscriptions.Count >= _settings.MaxRemoteSubscriptionsPerPeer ||
            bytes > _settings.MaxRemoteSubscriptionTopicBytesPerPeer - peer.RemoteSubscriptionTopicBytes ||
            !TryMakeRoomForRemoteSubscription(topic, bytes))
        {
            return false;
        }

        // Share the retained name across peers and derived indexes, including when the peer
        // that first announced it leaves before the others.
        if (!_remoteSubscriptionTopics.TryGetValue(topic, out retainedTopic!))
        {
            _remoteSubscriptionTopics.Add(topic);
            retainedTopic = topic;
        }
        peer.RemoteSubscriptions.Add(retainedTopic);
        peer.RemoteSubscriptionTopicBytes += bytes;
        _remoteSubscriptionCount++;
        _remoteSubscriptionTopicBytes += bytes;
        (peer.IsGossipSub ? gPeers : fPeers).GetOrAdd(retainedTopic, _ => []).Add(peer.PeerId);
        if (!IsLocallySubscribed(retainedTopic))
        {
            TrackUnwantedRemoteSubscription(peer, retainedTopic);
        }
        return true;
    }

    private bool HasRemoteSubscriptionCapacity(long bytes) =>
        _remoteSubscriptionCount < _settings.MaxRemoteSubscriptions &&
        bytes <= _settings.MaxRemoteSubscriptionTopicBytes - _remoteSubscriptionTopicBytes;

    private bool TryMakeRoomForRemoteSubscription(string topic, long bytes)
    {
        if (HasRemoteSubscriptionCapacity(bytes))
        {
            return true;
        }

        // Unwanted announcements cannot crowd out local interests. Check that reclaiming
        // unwanted state can fit the entire membership before evicting anything.
        if (!IsLocallySubscribed(topic) ||
            _remoteSubscriptionCount - _unwantedRemoteSubscriptions.Count >= _settings.MaxRemoteSubscriptions ||
            bytes > _settings.MaxRemoteSubscriptionTopicBytes - (_remoteSubscriptionTopicBytes - _unwantedRemoteSubscriptionTopicBytes))
        {
            return false;
        }

        // Each removal is constant-time and releases one admitted membership. The work is
        // bounded by retained unwanted state, without repeatedly scanning protected topics.
        while (!HasRemoteSubscriptionCapacity(bytes))
        {
            (PubsubPeer victim, string victimTopic) = _unwantedRemoteSubscriptionOrder.First!.Value;
            RemoveRemoteSubscription(victim, victimTopic);
        }
        return true;
    }

    private void TrackUnwantedRemoteSubscription(PubsubPeer peer, string topic)
    {
        var key = (peer.PeerId, topic);
        if (!_unwantedRemoteSubscriptions.ContainsKey(key))
        {
            _unwantedRemoteSubscriptions.Add(key, _unwantedRemoteSubscriptionOrder.AddLast((peer, topic)));
            _unwantedRemoteSubscriptionTopicBytes += 2L * topic.Length;
        }
    }

    private void ForgetUnwantedRemoteSubscription(PubsubPeer peer, string topic)
    {
        if (_unwantedRemoteSubscriptions.Remove((peer.PeerId, topic), out var node))
        {
            _unwantedRemoteSubscriptionOrder.Remove(node);
            _unwantedRemoteSubscriptionTopicBytes -= 2L * topic.Length;
        }
    }

    // Called under the router lock immediately after the local subscription changes.
    private void UpdateRemoteSubscriptionPriority(string topic)
    {
        foreach (PeerId peerId in (gPeers.GetValueOrDefault(topic) ?? []).Concat(fPeers.GetValueOrDefault(topic) ?? []))
        {
            if (peerState.TryGetValue(peerId, out PubsubPeer? peer) &&
                peer.RemoteSubscriptions.TryGetValue(topic, out string? retainedTopic))
            {
                if (IsLocallySubscribed(topic))
                {
                    ForgetUnwantedRemoteSubscription(peer, retainedTopic);
                }
                else
                {
                    TrackUnwantedRemoteSubscription(peer, retainedTopic);
                }
            }
        }
    }

    private void RemoveRemoteSubscription(PubsubPeer peer, string topic)
    {
        // Unknown unsubscribe must not create a topic, metadata, or an accounting charge.
        if (!peer.RemoteSubscriptions.TryGetValue(topic, out string? retainedTopic))
        {
            return;
        }

        peer.RemoteSubscriptions.Remove(retainedTopic);
        ForgetUnwantedRemoteSubscription(peer, retainedTopic);
        long bytes = 2L * retainedTopic.Length;
        peer.RemoteSubscriptionTopicBytes -= bytes;
        _remoteSubscriptionCount--;
        _remoteSubscriptionTopicBytes -= bytes;
        peer.RemovePartialMessagesSubscription(retainedTopic);

        var table = peer.IsGossipSub ? gPeers : fPeers;
        if (table.TryGetValue(retainedTopic, out HashSet<PeerId>? peers))
        {
            peers.Remove(peer.PeerId);
            if (peers.Count == 0)
            {
                table.TryRemove(retainedTopic, out _);
            }
        }
        if (!gPeers.ContainsKey(retainedTopic) && !fPeers.ContainsKey(retainedTopic))
        {
            _remoteSubscriptionTopics.Remove(retainedTopic);
        }

        if (mesh.TryGetValue(retainedTopic, out HashSet<PeerId>? meshPeers) && meshPeers.Remove(peer.PeerId))
        {
            RecordPeerLeaveMesh(peer.PeerId, retainedTopic);
        }
        if (fanout.TryGetValue(retainedTopic, out HashSet<PeerId>? fanoutPeers))
        {
            fanoutPeers.Remove(peer.PeerId);
        }
    }

    private void RemoveRemoteSubscriptions(PubsubPeer peer)
    {
        foreach (string topic in peer.RemoteSubscriptions.ToArray())
        {
            RemoveRemoteSubscription(peer, topic);
        }
    }
}
