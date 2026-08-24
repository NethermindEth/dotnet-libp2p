// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;
using System.Text;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter : IRoutingStateContainer, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal void OnRpc(PeerId peerId, Rpc rpc)
    {
        try
        {
            ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
            List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages = [];
            List<(string Topic, PeerId PeerId, PartialMessage Message)> receivedPartialMessages = [];
            lock (this)
            {
                HandleExtensions(peerId, rpc);

                if (rpc.Publish.Count != 0)
                {
                    HandleNewMessages(peerId, rpc.Publish, peerMessages, receivedMessages);
                }

                if (rpc.Subscriptions.Count != 0)
                {
                    HandleSubscriptions(peerId, rpc.Subscriptions);
                }

                if (rpc.Partial is not null)
                {
                    HandlePartialMessage(peerId, rpc.Partial, receivedPartialMessages);
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
            foreach ((string topic, PeerId receivedFrom, byte[] data) in receivedMessages)
            {
                OnMessage?.Invoke(topic, receivedFrom, data);
            }

            foreach ((string topic, PeerId receivedFrom, PartialMessage message) in receivedPartialMessages)
            {
                OnPartialMessage?.Invoke(topic, receivedFrom, message);
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

    private void HandleExtensions(PeerId peerId, Rpc rpc)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        ControlExtensions? extensions = rpc.Control?.Extensions;
        if (!peer.SupportsExtensions)
        {
            if (extensions is not null)
            {
                ApplyBehaviorPenalty(peerId, 1.0);
                logger?.LogDebug("Ignoring Gossipsub v1.3 extensions from {peerId} on {protocol}", peerId, peer.Protocol);
            }

            return;
        }

        if (peer.ReceivedFirstRpc)
        {
            if (extensions is not null)
            {
                ApplyBehaviorPenalty(peerId, 1.0);
                logger?.LogDebug("Ignoring repeated Gossipsub v1.3 extensions from {peerId}", peerId);
            }

            return;
        }

        peer.ReceivedFirstRpc = true;
        peer.SupportsPartialMessagesExtension = extensions?.PartialMessages ?? false;
    }

    private void HandlePartialMessage(PeerId peerId, PartialMessagesExtension partialMessage, List<(string Topic, PeerId PeerId, PartialMessage Message)> receivedPartialMessages)
    {
        if (!_settings.EnablePartialMessages ||
            !peerState.TryGetValue(peerId, out PubsubPeer? peer) ||
            !peer.SupportsPartialMessagesExtension)
        {
            return;
        }

        if (!partialMessage.HasTopicID ||
            !partialMessage.HasGroupID ||
            (!partialMessage.HasPartialMessage && !partialMessage.HasPartsMetadata))
        {
            logger?.LogDebug("Ignoring an incomplete Partial Messages extension payload from {peerId}", peerId);
            return;
        }

        if (!TryDecodeTopicId(partialMessage.TopicID, out string topicId))
        {
            logger?.LogDebug("Ignoring a Partial Messages extension payload with a non-UTF-8 topic from {peerId}", peerId);
            return;
        }

        receivedPartialMessages.Add((
            topicId,
            peerId,
            new PartialMessage(
                topicId,
                partialMessage.GroupID.ToByteArray(),
                partialMessage.HasPartialMessage ? partialMessage.PartialMessage.ToByteArray() : null,
                partialMessage.HasPartsMetadata ? partialMessage.PartsMetadata.ToByteArray() : null)));
    }

    private void HandleNewMessages(PeerId peerId, IEnumerable<Message> messages, ConcurrentDictionary<PeerId, Rpc> peerMessages, List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages)
    {
        // Check if peer is graylisted (Gossipsub v1.1)
        if (!IsDirectPeer(peerId) && ShouldGraylistPeer(peerId))
        {
            logger?.LogDebug("Ignoring messages from graylisted peer {peerId}", peerId);
            return;
        }

        if (logger?.IsEnabled(LogLevel.Trace) is true)
        {
            int knownMessages = messages.Select(_settings.GetMessageId).Count(messageId => _limboMessageCache.Contains(messageId) || _seenMessages.Contains(messageId));
            logger?.LogTrace($"Messages received: {messages.Count()}, already known: {knownMessages}. All: {string.Join(",", messages.Select(_settings.GetMessageId))}.");
        }
        else
        {
            logger?.LogDebug($"Messages received: {messages.Count()}");
        }

        foreach (Message? message in messages)
        {
            MessageId messageId = _settings.GetMessageId(message);

            if (_limboMessageCache.Contains(messageId) || _seenMessages.Contains(messageId))
            {
                continue;
            }

            MessageValidity validity = VerifyMessage?.Invoke(message) ?? MessageValidity.Accepted;

            switch (validity)
            {
                case MessageValidity.Rejected:
                    _limboMessageCache.Add(messageId);
                    _iwantPromises.Fulfill(messageId);
                    RecordMessageDelivery(peerId, message, message.Topic, false);  // Track invalid message
                    continue;
                case MessageValidity.Ignored:
                    _limboMessageCache.Add(messageId);
                    _iwantPromises.Fulfill(messageId);
                    continue;
                case MessageValidity.Throttled:
                    _iwantPromises.Clear(peerId);
                    continue;
            }

            if (!message.VerifySignature(_settings.DefaultSignaturePolicy))
            {
                _limboMessageCache.Add(messageId);
                RecordMessageDelivery(peerId, message, message.Topic, false);  // Track invalid message
                continue;
            }

            _seenMessages.Add(messageId);
            _messageCache.Put(messageId, message);
            _iwantPromises.Fulfill(messageId);

            // Record valid message delivery for scoring
            RecordMessageDelivery(peerId, message, message.Topic, true);

            PeerId author = new(message.From.ToArray());
            receivedMessages.Add((message.Topic, peerId, message.Data.ToByteArray()));

            foreach (PeerId directPeerId in GetDirectPeersForTopic(message.Topic))
            {
                if (directPeerId != author && directPeerId != peerId && ShouldSendFullMessage(directPeerId, message.Topic))
                {
                    peerMessages.GetOrAdd(directPeerId, _ => new Rpc()).Publish.Add(message);
                }
            }

            if (fPeers.TryGetValue(message.Topic, out HashSet<PeerId>? topicPeers))
            {
                foreach (PeerId peer in topicPeers)
                {
                    if (peer == author || peer == peerId || IsDirectPeer(peer))
                    {
                        continue;
                    }
                    if (ShouldSendFullMessage(peer, message.Topic))
                    {
                        peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                    }
                }
            }
            if (mesh.TryGetValue(message.Topic, out topicPeers))
            {
                foreach (PeerId peer in topicPeers)
                {
                    if (peer == author || peer == peerId || IsDirectPeer(peer))
                    {
                        continue;
                    }

                    // Only forward to peers above publish threshold (Gossipsub v1.1)
                    if (GetPeerScore(peer) >= _settings.PublishThreshold && ShouldSendFullMessage(peer, message.Topic))
                    {
                        peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                    }
                }
            }
        }
    }

    private static bool TryDecodeTopicId(ByteString topicIdBytes, out string topicId)
    {
        try
        {
            topicId = StrictUtf8.GetString(topicIdBytes.Span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            topicId = string.Empty;
            return false;
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

                if (_settings.EnablePartialMessages && state.SupportsPartialMessagesExtension)
                {
                    bool requestsPartialMessages = sub.RequestsPartial;
                    state.UpdatePartialMessagesSubscription(
                        sub.Topicid,
                        requestsPartialMessages,
                        sub.SupportsSendingPartial);
                }
            }
            else
            {
                state.RemovePartialMessagesSubscription(sub.Topicid);
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
            if (IsDirectPeer(peerId))
            {
                logger?.LogWarning("Rejecting GRAFT from direct peer {peerId} for topic {topic}", peerId, graft.TopicID);
                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                    .Ensure(r => r.Control.Prune)
                    .Add(new ControlPrune { TopicID = graft.TopicID, Backoff = (ulong)Math.Max(1, _settings.PruneBackoff / 1_000) });
                continue;
            }

            if (topicState.GetValueOrDefault(graft.TopicID)?.IsSubscribed is not true ||
                !mesh.TryGetValue(graft.TopicID, out HashSet<PeerId>? topicMesh))
            {
                logger?.LogDebug("Ignoring GRAFT from {peerId} for inactive topic {topic}", peerId, graft.TopicID);
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

            if (topicMesh.Count >= _settings.HighestDegree)
            {
                ControlPrune prune = new() { TopicID = graft.TopicID, Backoff = (ulong)(_settings.PruneBackoff / 1000) };

                if (peerState.TryGetValue(peerId, out PubsubPeer? peerData) && peerData.SupportsPeerExchange)
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
            if (topicState.GetValueOrDefault(prune.TopicID)?.IsSubscribed is true &&
                mesh.TryGetValue(prune.TopicID, out HashSet<PeerId>? topicMesh) &&
                topicMesh.Contains(peerId))
            {
                if (peerState.TryGetValue(peerId, out PubsubPeer? state))
                {
                    ulong backoffSeconds = prune.Backoff == 0 ? (ulong)(_settings.PruneBackoff / 1000) : prune.Backoff;
                    state.Backoff[prune.TopicID] = DateTime.Now.AddSeconds(backoffSeconds);
                }
                topicMesh.Remove(peerId);
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
        if (GetPeerScore(peerId) < _settings.GossipThreshold || !peerState.TryGetValue(peerId, out PubsubPeer? peer) || !peer.IsGossipSub)
        {
            return;
        }

        PeerControlState control = peer.Control;
        if (control.IHaveRequested >= _settings.MaxIHaveLength)
        {
            return;
        }

        HashSet<MessageId> messageIds = [];
        foreach (ControlIHave ihave in ihaves)
        {
            if (!control.TryAcceptIHave(_settings.MaxIHaveMessages))
            {
                break;
            }

            if (!mesh.ContainsKey(ihave.TopicID))
            {
                continue;
            }

            foreach (ByteString idBytes in ihave.MessageIDs.Take(_settings.MaxIHaveLength))
            {
                MessageId messageId = new(idBytes.ToByteArray());
                if (!_seenMessages.Contains(messageId) && !_limboMessageCache.Contains(messageId))
                {
                    messageIds.Add(messageId);
                    if (messageIds.Count == _settings.MaxIHaveLength)
                    {
                        break;
                    }
                }
            }

            if (messageIds.Count == _settings.MaxIHaveLength)
            {
                break;
            }
        }

        int requested = control.ReserveIHaveRequests(messageIds.Count, _settings.MaxIHaveLength);
        if (requested == 0)
        {
            return;
        }

        MessageId[] selected = SampleMessageIds(messageIds, requested);
        ControlIWant iwant = new();
        iwant.MessageIDs.AddRange(selected.Select(messageId => ByteString.CopyFrom(messageId.Bytes)));
        peerMessages.GetOrAdd(peerId, _ => new Rpc())
            .Ensure(r => r.Control.Iwant)
            .Add(iwant);
        _iwantPromises.Add(peerId, selected, DateTime.UtcNow.AddMilliseconds(_settings.IWantFollowupTime));
    }

    private void HandleIwant(PeerId peerId, IEnumerable<ControlIWant> iwants, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        if (GetPeerScore(peerId) < _settings.GossipThreshold || !peerState.TryGetValue(peerId, out PubsubPeer? peer) || !peer.IsGossipSub)
        {
            return;
        }

        PeerControlState control = peer.Control;
        HashSet<MessageId> requested = [];
        foreach (ControlIWant iwant in iwants)
        {
            if (!control.TryAcceptIwant(_settings.MaxIwantMessages))
            {
                break;
            }

            foreach (ByteString idBytes in iwant.MessageIDs)
            {
                if (requested.Count >= _settings.MaxIwantLength)
                {
                    break;
                }

                requested.Add(new MessageId(idBytes.ToByteArray()));
            }

            if (requested.Count >= _settings.MaxIwantLength)
            {
                break;
            }
        }

        List<Message> messages = [];
        long responseBytes = 0;
        foreach (MessageId messageId in requested)
        {
            if (peer.Control.IsUnwanted(messageId) || !_messageCache.TryGet(messageId, out Message message))
            {
                continue;
            }

            int messageSize = message.CalculateSize();
            int serializedMessageSize = CodedOutputStream.ComputeTagSize(Rpc.PublishFieldNumber)
                + CodedOutputStream.ComputeLengthSize(messageSize)
                + messageSize;
            if (responseBytes + serializedMessageSize > _settings.MaxIwantResponseBytes)
            {
                continue;
            }

            if (control.TryRecordIwantResponse(messageId, heartbeatTick, _settings.mcache_len, _settings.GossipRetransmission, _settings.MaxIwantLength))
            {
                messages.Add(message);
                responseBytes += serializedMessageSize;
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
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer) || peer.Protocol < PubsubPeer.PubsubProtocol.GossipsubV12)
        {
            return;
        }

        PeerControlState control = peer.Control;
        int maxUnwanted = (int)Math.Min(
            (long)_settings.MaxIdontwantMessages * _settings.MaxIdontwantLength * _settings.IdontwantTtlHeartbeats,
            int.MaxValue);
        foreach (ControlIDontWant idontwant in idontwants)
        {
            if (!control.TryAcceptIdontwant(_settings.MaxIdontwantMessages))
            {
                break;
            }

            foreach (ByteString idBytes in idontwant.MessageIDs.Take(_settings.MaxIdontwantLength))
            {
                control.AddUnwanted(new MessageId(idBytes.ToByteArray()), heartbeatTick + _settings.IdontwantTtlHeartbeats, maxUnwanted);
            }
        }
    }

    private static MessageId[] SampleMessageIds(IEnumerable<MessageId> messageIds, int count)
    {
        List<MessageId> sample = new(count);
        int candidates = 0;
        foreach (MessageId messageId in messageIds)
        {
            candidates++;
            if (sample.Count < count)
            {
                sample.Add(messageId);
                continue;
            }

            int replacementIndex = Random.Shared.Next(candidates);
            if (replacementIndex < count)
            {
                sample[replacementIndex] = messageId;
            }
        }

        return sample.ToArray();
    }
}
