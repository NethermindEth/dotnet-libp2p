// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter : IRoutingStateContainer, IDisposable
{
    internal void OnRpc(PeerId peerId, Rpc rpc, string? protocolId = null, bool isFirstRpc = true)
    {
        List<PendingValidation> deferredMessages = [];
        try
        {
            ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
            List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages = [];
            try
            {
                lock (this)
                {
                    if (_stopped.IsCancellationRequested)
                    {
                        return;
                    }

                    RemoveExpiredPendingValidations();
                    HandleExtensions(peerId, rpc, protocolId, isFirstRpc);

                    if (rpc.Publish.Count != 0)
                    {
                        HandleNewMessages(peerId, rpc.Publish, peerMessages, receivedMessages, deferredMessages);
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
            }
            finally
            {
                DispatchDeferredMessages(deferredMessages);
            }
            DispatchMessages(receivedMessages, peerMessages);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Exception while processing RPC");
        }
    }

    private void DispatchDeferredMessages(List<PendingValidation> deferredMessages)
    {
        foreach (PendingValidation pending in deferredMessages)
        {
            Func<PeerId, Message, Task>? callback = OnDeferredMessage;
            if (callback is null)
            {
                RemovePendingValidation(pending);
                continue;
            }

            try
            {
                lock (this)
                {
                    if (_stopped.IsCancellationRequested ||
                        !_pendingValidations.TryGetValue(pending.Id, out PendingValidation? current) ||
                        !ReferenceEquals(current, pending))
                    {
                        continue;
                    }

                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    if (pending.ExpiresAt <= now)
                    {
                        _pendingValidations.Remove(pending.Id);
                        _pendingValidationBytes -= pending.Size;
                        continue;
                    }

                    if (!TryGetPendingValidationExpiry(now, out DateTimeOffset expiresAt))
                    {
                        throw new ArgumentOutOfRangeException(nameof(PubsubSettings.PendingValidationTimeout));
                    }
                    pending.ExpiresAt = expiresAt;
                }

                Task validation = callback(pending.Source, pending.Original);
                ArgumentNullException.ThrowIfNull(validation);
                _ = ObserveDeferredValidation(validation, new WeakReference<PendingValidation>(pending), pending.Snapshot.Topic);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Deferred pubsub validator failed for topic {topic}", pending.Snapshot.Topic);
                RemovePendingValidation(pending);
            }
        }
    }

    private void RemovePendingValidation(PendingValidation pending)
    {
        lock (this)
        {
            if (_pendingValidations.TryGetValue(pending.Id, out PendingValidation? current) && ReferenceEquals(current, pending))
            {
                _pendingValidations.Remove(pending.Id);
                _pendingValidationBytes -= pending.Size;
            }
        }
    }

    private bool TryGetPendingValidationExpiry(DateTimeOffset now, out DateTimeOffset expiresAt)
    {
        TimeSpan timeout = _settings.PendingValidationTimeout;
        if (timeout <= TimeSpan.Zero || timeout > DateTimeOffset.MaxValue - now)
        {
            expiresAt = default;
            return false;
        }

        expiresAt = now.Add(timeout);
        return true;
    }

    private async Task ObserveDeferredValidation(Task validation, WeakReference<PendingValidation> pendingReference, string topic)
    {
        try
        {
            await validation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (validation.IsCanceled)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Deferred pubsub validator failed for topic {topic}", topic);
        }
        finally
        {
            if (pendingReference.TryGetTarget(out PendingValidation? pending))
            {
                RemovePendingValidation(pending);
            }
        }
    }

    /// <summary>
    /// Completes a message for which <see cref="VerifyMessage"/> returned
    /// <see cref="MessageValidity.Deferred"/>. The exact message instance passed to the
    /// validator is required, so a late completion cannot finish a replacement with the same ID.
    /// Returns false if it was not pending, expired, or was changed after validation began.
    /// </summary>
    public bool CompleteValidation(Message message, MessageValidity validity)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (validity is MessageValidity.Deferred || !Enum.IsDefined(validity))
        {
            throw new ArgumentOutOfRangeException(nameof(validity));
        }

        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
        List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages = [];
        lock (this)
        {
            if (_stopped.IsCancellationRequested)
            {
                return false;
            }

            RemoveExpiredPendingValidations();
            KeyValuePair<MessageId, PendingValidation> entry = _pendingValidations.FirstOrDefault(
                pair => ReferenceEquals(pair.Value.Original, message));
            PendingValidation? pending = entry.Value;
            if (pending is null)
            {
                return false;
            }

            _pendingValidations.Remove(entry.Key);
            _pendingValidationBytes -= pending.Size;
            if (!message.Equals(pending.Snapshot))
            {
                return false;
            }

            switch (validity)
            {
                case MessageValidity.Accepted:
                    AcceptMessage(entry.Key, pending.Snapshot, pending.Source, peerMessages, receivedMessages);
                    break;
                case MessageValidity.Rejected:
                    _limboMessageCache.Add(entry.Key, new(entry.Key, pending.Snapshot));
                    RecordMessageDelivery(pending.Source, pending.Snapshot, pending.Snapshot.Topic, false);
                    break;
                case MessageValidity.Ignored:
                    _limboMessageCache.Add(entry.Key, new(entry.Key, pending.Snapshot));
                    break;
            }
        }

        DispatchMessages(receivedMessages, peerMessages);
        return true;
    }

    private void RemoveExpiredPendingValidations()
    {
        if (_pendingValidations.Count == 0)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (MessageId messageId in _pendingValidations
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key).ToArray())
        {
            _pendingValidationBytes -= _pendingValidations[messageId].Size;
            _pendingValidations.Remove(messageId);
        }
    }

    private void DispatchMessages(List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages,
        ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        foreach ((string topic, PeerId receivedFrom, byte[] data) in receivedMessages)
        {
            foreach (Delegate handler in OnMessage?.GetInvocationList() ?? [])
            {
                try
                {
                    ((Action<string, PeerId, byte[]>)handler)(topic, receivedFrom, data);
                }
                catch (Exception ex)
                {
                    LogSubscriberError(ex, topic);
                }
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
        }
    }

    internal void LogSubscriberError(Exception ex, string topic)
        => logger?.LogWarning(ex, "Pubsub message subscriber failed for topic {topic}", topic);

    private void HandleExtensions(PeerId peerId, Rpc rpc, string? protocolId, bool isFirstRpc)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        ControlExtensions? extensions = rpc.Control?.Extensions;
        if (protocolId is null ? !peer.SupportsExtensions : protocolId != GossipsubProtocolVersionV13)
        {
            if (extensions is not null)
            {
                logger?.LogDebug("Ignoring Gossipsub v1.3 extensions from {peerId} on {protocol}", peerId, peer.Protocol);
            }

            return;
        }

        if (!isFirstRpc && extensions is not null)
        {
            ApplyBehaviorPenalty(peerId, 1.0);
            logger?.LogDebug("Ignoring repeated Gossipsub v1.3 extensions from {peerId}", peerId);
        }
    }

    private void HandleNewMessages(PeerId peerId, IEnumerable<Message> messages, ConcurrentDictionary<PeerId, Rpc> peerMessages,
        List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages, List<PendingValidation> deferredMessages)
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

            if (_pendingValidations.ContainsKey(messageId) || _limboMessageCache.Contains(messageId) || _messageCache.Contains(messageId))
            {
                continue;
            }

            if (!message.VerifySignature(_settings.DefaultSignaturePolicy))
            {
                // An unauthenticated copy can share the application message ID of a valid message.
                RecordMessageDelivery(peerId, message, message.Topic, false);  // Track invalid message
                continue;
            }

            Func<PeerId, Message, MessageValidity>? verify = VerifyMessage;
            Message? authenticated = verify is null ? null : message.Clone();
            MessageValidity validity = verify?.Invoke(peerId, message) ?? MessageValidity.Accepted;
            if (authenticated is not null && !message.Equals(authenticated))
            {
                logger?.LogWarning("Pubsub validator changed a message after authentication for topic {topic}", authenticated.Topic);
                continue;
            }

            Message validated = authenticated ?? message;

            switch (validity)
            {
                case MessageValidity.Rejected:
                    _limboMessageCache.Add(messageId, new(messageId, validated));
                    RecordMessageDelivery(peerId, validated, validated.Topic, false);  // Track invalid message
                    continue;
                case MessageValidity.Ignored:
                    _limboMessageCache.Add(messageId, new(messageId, validated));
                    continue;
                case MessageValidity.Throttled:
                    continue;
            }

            if (validity == MessageValidity.Deferred)
            {
                if (OnDeferredMessage is null)
                {
                    continue;
                }

                int size = validated.CalculateSize();
                if (_pendingValidations.Count >= _settings.MaxPendingValidationMessages ||
                    size > _settings.MaxPendingValidationBytes - _pendingValidationBytes)
                {
                    continue;
                }

                if (!TryGetPendingValidationExpiry(_timeProvider.GetUtcNow(), out DateTimeOffset expiresAt))
                {
                    continue;
                }

                PendingValidation pending = new(messageId, message, validated, peerId, size) { ExpiresAt = expiresAt };
                _pendingValidations.Add(messageId, pending);
                _pendingValidationBytes += size;
                deferredMessages.Add(pending);
                continue;
            }

            if (validity != MessageValidity.Accepted)
            {
                continue;
            }

            AcceptMessage(messageId, validated, peerId, peerMessages, receivedMessages);
        }
    }

    private void AcceptMessage(MessageId messageId, Message message, PeerId peerId,
        ConcurrentDictionary<PeerId, Rpc> peerMessages, List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages)
    {
        _messageCache.Add(messageId, new(messageId, message));
        RecordMessageDelivery(peerId, message, message.Topic, true);

        PeerId author = new(message.From.ToArray());
        receivedMessages.Add((message.Topic, peerId, message.Data.ToByteArray()));

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
        List<MessageId> messageIds = [];

        foreach (ControlIHave? ihave in ihaves.Where(iw => topicState.GetValueOrDefault(iw.TopicID)?.IsSubscribed is true))
        {
            messageIds.AddRange(ihave.MessageIDs.Select(m => new MessageId(m.ToByteArray()))
                .Where(mid => !_pendingValidations.ContainsKey(mid) && !_messageCache.Contains(mid)));
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
            _idontwantMessages.Add((peerId, messageId));
        }
    }
}
