// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter : IRoutingStateContainer, IDisposable
{
    internal void OnRpc(PeerId peerId, Rpc rpc)
    {
        try
        {
            ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
            lock (this)
            {
                if (rpc.Publish.Count != 0)
                {
                    HandleNewMessages(peerId, rpc.Publish, peerMessages);
                }

                if (rpc.Subscriptions.Count != 0)
                {
                    HandleSubscriptions(peerId, rpc.Subscriptions);
                }

                if (rpc.Control is not null)
                {
                    if (rpc.Control.Graft.Count != 0)
                    {
                        HandleGraft(peerId, rpc.Control.Graft, peerMessages);
                    }

                    if (rpc.Control.Prune.Count != 0)
                    {
                        HandlePrune(peerId, rpc.Control.Prune, peerMessages);
                    }

                    if (rpc.Control.Ihave.Count != 0)
                    {
                        HandleIhave(peerId, rpc.Control.Ihave, peerMessages);
                    }

                    if (rpc.Control.Iwant.Count != 0)
                    {
                        HandleIwant(peerId, rpc.Control.Iwant, peerMessages);
                    }

                    if (rpc.Control.Idontwant.Count != 0)
                    {
                        HandleIdontwant(peerId, rpc.Control.Idontwant);
                    }
                }
            }
            foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
            {
                peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Exception while processing RPC");
        }
    }

    private void HandleNewMessages(PeerId peerId, IEnumerable<Message> messages, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        // Check if peer is graylisted (Gossipsub v1.1)
        if (ShouldGraylistPeer(peerId))
        {
            logger?.LogDebug("Ignoring messages from graylisted peer {peerId}", peerId);
            return;
        }

        if (logger?.IsEnabled(LogLevel.Trace) is true)
        {
            int knownMessages = messages.Select(_settings.GetMessageId).Count(messageId => _limboMessageCache.Contains(messageId) || _messageCache!.Contains(messageId));
            logger?.LogTrace($"Messages received: {messages.Count()}, already known: {knownMessages}. All: {string.Join(",", messages.Select(_settings.GetMessageId))}.");
        }
        else
        {
            logger?.LogDebug($"Messages received: {messages.Count()}");
        }

        foreach (Message? message in messages)
        {
            MessageId messageId = _settings.GetMessageId(message);

            if (_limboMessageCache.Contains(messageId) || _messageCache!.Contains(messageId))
            {
                continue;
            }

            MessageValidity validity = VerifyMessage?.Invoke(message) ?? MessageValidity.Accepted;

            switch (validity)
            {
                case MessageValidity.Rejected:
                    _limboMessageCache.Add(messageId, new(messageId, message));
                    RecordMessageDelivery(peerId, message, message.Topic, false);  // Track invalid message
                    continue;
                case MessageValidity.Ignored:
                    _limboMessageCache.Add(messageId, new(messageId, message));
                    continue;
                case MessageValidity.Throttled:
                    continue;
            }

            if (!message.VerifySignature(_settings.DefaultSignaturePolicy))
            {
                _limboMessageCache!.Add(messageId, new(messageId, message));
                RecordMessageDelivery(peerId, message, message.Topic, false);  // Track invalid message
                continue;
            }

            _messageCache.Add(messageId, new(messageId, message));

            // Record valid message delivery for scoring
            RecordMessageDelivery(peerId, message, message.Topic, true);

            PeerId author = new(message.From.ToArray());
            OnMessage?.Invoke(message.Topic, message.Data.ToByteArray());

            if (fPeers.TryGetValue(message.Topic, out HashSet<PeerId>? topicPeers))
            {
                foreach (PeerId peer in topicPeers)
                {
                    if (peer == author || peer == peerId)
                    {
                        continue;
                    }
                    peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                }
            }
            if (mesh.TryGetValue(message.Topic, out topicPeers))
            {
                foreach (PeerId peer in topicPeers)
                {
                    if (peer == author || peer == peerId)
                    {
                        continue;
                    }

                    // Only forward to peers above publish threshold (Gossipsub v1.1)
                    if (GetPeerScore(peer) >= _settings.PublishThreshold)
                    {
                        peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                    }
                }
            }
        }
    }

    private void HandleSubscriptions(PeerId peerId, IEnumerable<Rpc.Types.SubOpts> subscriptions)
    {
        foreach (Rpc.Types.SubOpts? sub in subscriptions)
        {
            PubsubPeer? state = peerState.GetValueOrDefault(peerId);
            if (state is null)
            {
                return;
            }
            if (sub.Subscribe)
            {
                if (state.IsGossipSub)
                {
                    gPeers.GetOrAdd(sub.Topicid, _ => []).Add(peerId);
                }
                else if (state.IsFloodSub)
                {
                    fPeers.GetOrAdd(sub.Topicid, _ => []).Add(peerId);
                }
            }
            else
            {
                if (state.IsGossipSub)
                {
                    gPeers.GetOrAdd(sub.Topicid, _ => []).Remove(peerId);
                    if (mesh.ContainsKey(sub.Topicid))
                    {
                        if (mesh[sub.Topicid].Remove(peerId))
                        {
                            RecordPeerLeaveMesh(peerId, sub.Topicid);  // Track for scoring
                        }
                    }
                    if (fanout.ContainsKey(sub.Topicid))
                    {
                        fanout[sub.Topicid].Remove(peerId);
                    }
                }
                else if (state.IsFloodSub)
                {
                    fPeers.GetOrAdd(sub.Topicid, _ => []).Remove(peerId);
                }
            }
        }
    }

    private void HandleGraft(PeerId peerId, IEnumerable<ControlGraft> grafts, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        foreach (ControlGraft? graft in grafts)
        {
            if (!topicState.ContainsKey(graft.TopicID))
            {
                // Ignore GRAFT for unknown topics (spam protection)
                logger?.LogDebug("Ignoring GRAFT from {peerId} for unknown topic {topic}", peerId, graft.TopicID);
                continue;
            }

            // Check if peer is in backoff period
            if (peerState.TryGetValue(peerId, out PubsubPeer? state))
            {
                if (state.Backoff.TryGetValue(graft.TopicID, out DateTime backoffUntil) && backoffUntil > DateTime.Now)
                {
                    // Peer grafting during backoff - apply behavioral penalty and auto-prune
                    logger?.LogDebug("Peer {peerId} attempted GRAFT during backoff for topic {topic}", peerId, graft.TopicID);
                    ApplyBehaviorPenalty(peerId, 1.0);

                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                        .Ensure(r => r.Control.Prune)
                        .Add(new ControlPrune { TopicID = graft.TopicID, Backoff = (ulong)(_settings.PruneBackoff / 1000) });
                    continue;
                }
            }

            HashSet<PeerId> topicMesh = mesh[graft.TopicID];

            if (topicMesh.Count >= _settings.HighestDegree)
            {
                ControlPrune prune = new() { TopicID = graft.TopicID, Backoff = (ulong)(_settings.PruneBackoff / 1000) };

                if (peerState.TryGetValue(peerId, out PubsubPeer? peerData) && peerData.IsGossipSub && peerData.Protocol >= PubsubPeer.PubsubProtocol.GossipsubV11)
                {
                    peerData.Backoff[prune.TopicID] = DateTime.Now.AddMilliseconds(_settings.PruneBackoff);

                    // Only provide PX if peer score is above threshold
                    if (GetPeerScore(peerId) >= _settings.AcceptPXThreshold)
                    {
                        prune.Peers.AddRange(topicMesh.ToArray()
                            .Where(pid => GetPeerScore(pid) >= 0)  // Only exchange non-negative scoring peers
                            .Select(pid => (PeerId: pid, Record: _peerStore.GetPeerInfo(pid)?.SignedPeerRecord))
                            .Where(pid => pid.Record is not null)
                            .Select(pid => new PeerInfo
                            {
                                PeerID = ByteString.CopyFrom(pid.PeerId.Bytes),
                                SignedPeerRecord = pid.Record,
                            }));
                    }
                }

                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                    .Ensure(r => r.Control.Prune)
                    .Add(prune);
            }
            else
            {
                if (!topicMesh.Contains(peerId))
                {
                    topicMesh.Add(peerId);
                    gPeers[graft.TopicID].Add(peerId);
                    RecordPeerJoinMesh(peerId, graft.TopicID);  // Track for scoring
                }
            }
        }
    }

    private void HandlePrune(PeerId peerId, IEnumerable<ControlPrune> prunes, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        foreach (ControlPrune? prune in prunes)
        {
            if (topicState.ContainsKey(prune.TopicID) && mesh[prune.TopicID].Contains(peerId))
            {
                if (peerState.TryGetValue(peerId, out PubsubPeer? state))
                {
                    ulong backoffSeconds = prune.Backoff == 0 ? (ulong)(_settings.PruneBackoff / 1000) : prune.Backoff;
                    state.Backoff[prune.TopicID] = DateTime.Now.AddSeconds(backoffSeconds);
                }
                mesh[prune.TopicID].Remove(peerId);
                RecordPeerLeaveMesh(peerId, prune.TopicID);  // Track for scoring (P1 and P3b)

                // Handle PX (Peer Exchange) only if peer score is above threshold
                if (prune.Peers.Count > 0 && GetPeerScore(peerId) >= _settings.AcceptPXThreshold)
                {
                    logger?.LogDebug("Received {count} peer(s) via PX from {peerId} for topic {topic}", prune.Peers.Count, peerId, prune.TopicID);

                    foreach (PeerInfo? peer in prune.Peers)
                    {
                        _peerStore.Discover(peer.SignedPeerRecord);
                    }
                }
            }
        }
    }

    private void HandleIhave(PeerId peerId, IEnumerable<ControlIHave> ihaves, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        List<MessageId> messageIds = [];

        foreach (ControlIHave? ihave in ihaves.Where(iw => topicState.ContainsKey(iw.TopicID)))
        {
            messageIds.AddRange(ihave.MessageIDs.Select(m => new MessageId(m.ToByteArray()))
                .Where(mid => !_messageCache.Contains(mid)));
        }

        if (messageIds.Any())
        {
            ControlIWant ciw = new();
            foreach (MessageId mId in messageIds)
            {
                ciw.MessageIDs.Add(ByteString.CopyFrom(mId.Bytes));
            }
            peerMessages.GetOrAdd(peerId, _ => new Rpc())
                .Ensure(r => r.Control.Iwant)
                .Add(ciw);
        }
    }

    private void HandleIwant(PeerId peerId, IEnumerable<ControlIWant> iwants, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        IEnumerable<MessageId> messageIds = iwants.SelectMany(iw => iw.MessageIDs).Select(m => new MessageId(m.ToByteArray()));
        List<Message> messages = [];
        foreach (MessageId mId in messageIds)
        {
            Message message = _messageCache.Get(mId).Message;
            if (message != default)
            {
                messages.Add(message);
            }
        }
        if (messages.Any())
        {
            peerMessages.GetOrAdd(peerId, _ => new Rpc())
               .Publish.AddRange(messages);
        }
    }

    private void HandleIdontwant(PeerId peerId, IEnumerable<ControlIDontWant> idontwants)
    {
        foreach (MessageId messageId in idontwants.SelectMany(iw => iw.MessageIDs).Select(m => new MessageId(m.ToByteArray())).Take(_settings.MaxIdontwantMessages))
        {
            _dontWantMessages.Add((peerId, messageId));
        }
    }
}
