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
            _remoteSubscriptionCount >= _settings.MaxRemoteSubscriptions ||
            bytes > _settings.MaxRemoteSubscriptionTopicBytesPerPeer - peer.RemoteSubscriptionTopicBytes ||
            bytes > _settings.MaxRemoteSubscriptionTopicBytes - _remoteSubscriptionTopicBytes)
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
        return true;
    }

    private void RemoveRemoteSubscription(PubsubPeer peer, string topic)
    {
        // Unknown unsubscribe must not create a topic, metadata, or an accounting charge.
        if (!peer.RemoteSubscriptions.TryGetValue(topic, out string? retainedTopic))
        {
            return;
        }

        peer.RemoteSubscriptions.Remove(retainedTopic);
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
