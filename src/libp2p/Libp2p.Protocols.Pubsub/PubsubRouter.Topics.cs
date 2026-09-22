// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
                    if (gPeers[topicId].Contains(peerId) &&
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
                Rpc topicUpdate = new Rpc().WithTopics([topicId], []);
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

            ulong seqNo = this.seqNo++;
            Rpc rpc = new Rpc().WithMessages(topicId, seqNo, localPeer.Identity.PeerId.Bytes, message, localPeer.Identity);

            // Floodsub peers always get the message
            foreach (PeerId peerId in fPeers.GetValueOrDefault(topicId) ?? [])
            {
                peerState.GetValueOrDefault(peerId)?.Send(rpc);
            }

            // Gossipsub v1.1: Flood publishing
            if (_settings.FloodPublish && gPeers.TryGetValue(topicId, out HashSet<PeerId>? allGossipsubPeers))
            {
                // Send to all gossipsub peers above publish threshold
                foreach (PeerId peerId in allGossipsubPeers)
                {
                    if (GetPeerScore(peerId) >= _settings.PublishThreshold)
                    {
                        peerState.GetValueOrDefault(peerId)?.Send(rpc);
                    }
                }
            }
            else
            {
                // Standard gossipsub v1.0 behavior: send to mesh or fanout
                if (mesh.TryGetValue(topicId, out HashSet<PeerId>? meshPeers))
                {
                    foreach (PeerId peerId in meshPeers)
                    {
                        if (GetPeerScore(peerId) >= _settings.PublishThreshold)
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
                            var eligiblePeers = topicPeers.Where(p => GetPeerScore(p) >= 0).ToList();
                            foreach (PeerId peer in eligiblePeers.Take(_settings.Degree))
                            {
                                topicFanout.Add(peer);
                            }
                        }
                    }

                    foreach (PeerId peerId in topicFanout)
                    {
                        if (GetPeerScore(peerId) >= _settings.PublishThreshold)
                        {
                            peerState.GetValueOrDefault(peerId)?.Send(rpc);
                        }
                    }
                }
            }
        }
    }

}
