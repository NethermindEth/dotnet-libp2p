// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Buffers.Binary;
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
        PubsubPeer[] peers;
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

            if (fanout.TryRemove(topicId, out HashSet<PeerId>? fanoutPeers))
            {
                foreach (PeerId peerId in fanoutPeers.ToList())
                {
                    meshPeers.Add(peerId);
                }

                fanoutLastPublished.TryRemove(topicId, out _);
            }

            peers = peerState.Values.ToArray();
        }

        Rpc topicUpdate = new Rpc().WithTopics([topicId], []);
        foreach (PubsubPeer peer in peers)
        {
            peer.Send(topicUpdate);
        }
    }

    public void Unsubscribe(string topicId)
    {
        KeyValuePair<PeerId, PubsubPeer>[] peers;
        HashSet<PeerId>? removedMesh;
        lock (this)
        {
            if (!topicState.TryGetValue(topicId, out Topic? topic) || !topic.IsSubscribed)
            {
                return;
            }

            topic.IsSubscribed = false;

            if (mesh.TryRemove(topicId, out removedMesh))
            {
                foreach (PeerId peerId in removedMesh)
                {
                    RecordPeerLeaveMesh(peerId, topicId);
                }
            }

            fanout.TryRemove(topicId, out _);
            fanoutLastPublished.TryRemove(topicId, out _);
            peers = peerState.ToArray();
        }

        foreach ((PeerId peerId, PubsubPeer peer) in peers)
        {
            Rpc msg = new Rpc().WithTopics([], [topicId]);
            if (removedMesh?.Contains(peerId) is true)
            {
                msg.Ensure(r => r.Control.Prune).Add(new ControlPrune { TopicID = topicId });
            }

            peer.Send(msg);
        }
    }

    public void UnsubscribeAll()
    {
        foreach (string topicId in topicState
            .Where(pair => pair.Value.IsSubscribed)
            .Select(pair => pair.Key)
            .ToArray())
        {
            Unsubscribe(topicId);
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

        topicState.GetOrAdd(topicId, (id) => new Topic(this, topicId));

        ulong seqNo = this.seqNo++;
        Span<byte> seqNoBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        Rpc rpc = new Rpc().WithMessages(topicId, seqNo, localPeer.Identity.PeerId.Bytes, message, localPeer.Identity);

        // Floodsub peers always get the message
        foreach (PeerId peerId in fPeers[topicId])
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
            if (mesh.ContainsKey(topicId))
            {
                foreach (PeerId peerId in mesh[topicId].ToList())
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
