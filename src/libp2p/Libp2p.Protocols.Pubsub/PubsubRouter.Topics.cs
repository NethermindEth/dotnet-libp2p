// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
        topicState.GetOrAdd(topicId, (id) => new Topic(this, topicId)).IsSubscribed = true;

        if (!fPeers.TryAdd(topicId, []))
        {
            // Already exists
            return;
        }

        gPeers.TryAdd(topicId, []);

        HashSet<PeerId> meshPeers = mesh.GetOrAdd(topicId, []);

        if (fanout.TryGetValue(topicId, out HashSet<PeerId>? fanoutPeers))
        {
            foreach (PeerId peerId in fanoutPeers.ToList())
            {
                meshPeers.Add(peerId);
            }

            fanoutPeers.Clear();
        }

        Rpc topicUpdate = new Rpc().WithTopics([topicId], []);
        foreach (KeyValuePair<PeerId, PubsubPeer> peer in peerState)
        {
            peer.Value.Send(topicUpdate);
        }
    }

    public void Unsubscribe(string topicId)
    {
        topicState.GetOrAdd(topicId, (id) => new Topic(this, topicId)).IsSubscribed = false;

        foreach (PeerId peerId in fPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics([], [topicId]);

            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }

        foreach (PeerId peerId in gPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics([], [topicId]);

            if (mesh.TryGetValue(topicId, out HashSet<PeerId>? topicMesh) && topicMesh.Contains(peerId))
            {
                msg.Ensure(r => r.Control.Prune).Add(new ControlPrune { TopicID = topicId });
            }
            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }
    }

    public void UnsubscribeAll()
    {
        foreach (PeerId? peerId in fPeers.SelectMany(kv => kv.Value))
        {
            Rpc msg = new Rpc().WithTopics([], topicState.Keys);

            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }

        Dictionary<PeerId, Rpc> peerMessages = [];

        foreach (PeerId? peerId in gPeers.SelectMany(kv => kv.Value))
        {
            (peerMessages[peerId] ??= new Rpc())
                .WithTopics([], topicState.Keys);
        }

        foreach (KeyValuePair<string, HashSet<PeerId>> topicMesh in mesh)
        {
            foreach (PeerId peerId in topicMesh.Value)
            {
                (peerMessages[peerId] ??= new Rpc())
                   .Ensure(r => r.Control.Prune)
                   .Add(new ControlPrune { TopicID = topicMesh.Key });
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
        }
    }

    public void Publish(string topicId, byte[] message)
    {
        topicState.GetOrAdd(topicId, (id) => new Topic(this, topicId));

        ulong seqNo = this.seqNo++;
        Span<byte> seqNoBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        Rpc rpc = new Rpc().WithMessages(topicId, seqNo, localPeer!.Identity.PeerId.Bytes, message, localPeer.Identity);

        foreach (PeerId peerId in fPeers[topicId])
        {
            peerState.GetValueOrDefault(peerId)?.Send(rpc);
        }

        if (mesh.ContainsKey(topicId))
        {
            foreach (PeerId peerId in mesh[topicId].ToList())
            {
                peerState.GetValueOrDefault(peerId)?.Send(rpc);
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
                    foreach (PeerId peer in topicPeers.Take(_settings.Degree))
                    {
                        topicFanout.Add(peer);
                    }
                }
            }

            foreach (PeerId peerId in topicFanout)
            {
                peerState.GetValueOrDefault(peerId)?.Send(rpc);
            }
        }
    }

}
