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

    internal void OnRpc(PeerId peerId, Rpc rpc, string? protocolId = null, bool isFirstRpc = true)
    {
        List<PendingValidation> deferredMessages = [];
        try
        {
            ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
            Dictionary<PeerId, Rpc> idontwantMessages = [];
            List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages = [];
            List<(string Topic, PeerId PeerId, PartialMessage Message)> receivedPartialMessages = [];
            try
            {
                lock (this)
                {
                    if (_stopped.IsCancellationRequested)
                    {
                        return;
                    }

                    RemoveExpiredPendingValidations();

                    // Gossipsub v1.1: every RPC from a peer scored below GraylistThreshold is
                    // ignored, not only its messages. Direct peers are exempt from scoring.
                    if (!IsDirectPeer(peerId) && ShouldGraylistPeer(peerId))
                    {
                        logger?.LogDebug("Ignoring RPC from graylisted peer {peerId}", peerId);
                        return;
                    }

                    HandleExtensions(peerId, rpc, protocolId, isFirstRpc);

                    if (rpc.Publish.Count != 0)
                    {
                        HandleNewMessages(peerId, rpc.Publish, peerMessages, idontwantMessages, receivedMessages, deferredMessages);
                    }

                    if (rpc.Subscriptions.Count != 0)
                    {
                        HandleSubscriptions(peerId, rpc.Subscriptions);
                    }

                    if (rpc.Partial is not null)
                    {
                        HandlePartialMessage(peerId, rpc.Partial, protocolId, receivedPartialMessages);
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
                try
                {
                    DispatchMessages(receivedMessages, receivedPartialMessages, idontwantMessages, peerMessages);
                }
                finally
                {
                    DispatchDeferredMessages(deferredMessages);
                }
            }
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
        Dictionary<PeerId, Rpc> idontwantMessages = [];
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
                    AcceptMessage(entry.Key, pending.Snapshot, pending.Source, peerMessages, idontwantMessages, receivedMessages);
                    break;
                case MessageValidity.Rejected:
                    _limboMessageCache.Add(entry.Key);
                    RecordMessageDelivery(pending.Source, pending.Snapshot, pending.Snapshot.Topic, false);
                    break;
                case MessageValidity.Ignored:
                    _limboMessageCache.Add(entry.Key);
                    break;
            }
        }

        DispatchMessages(receivedMessages, [], idontwantMessages, peerMessages);
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
        List<(string Topic, PeerId PeerId, PartialMessage Message)> receivedPartialMessages,
        Dictionary<PeerId, Rpc> idontwantMessages, ConcurrentDictionary<PeerId, Rpc> peerMessages)
    {
        foreach ((PeerId recipient, Rpc idontwant) in idontwantMessages)
        {
            peerState.GetValueOrDefault(recipient)?.Send(idontwant);
        }

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

        foreach ((string topic, PeerId receivedFrom, PartialMessage message) in receivedPartialMessages)
        {
            foreach (Delegate handler in OnPartialMessage?.GetInvocationList() ?? [])
            {
                try
                {
                    ((Action<string, PeerId, PartialMessage>)handler)(topic, receivedFrom, message);
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
        if (isFirstRpc && extensions?.PartialMessages == true)
        {
            peer.SupportsPartialMessagesExtension = true;
        }
    }

    private void HandlePartialMessage(PeerId peerId, PartialMessagesExtension partialMessage, string? protocolId, List<(string Topic, PeerId PeerId, PartialMessage Message)> receivedPartialMessages)
    {
        if (!_settings.EnablePartialMessages ||
            protocolId is not null && protocolId != GossipsubProtocolVersionV13 ||
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

        topicState.TryGetValue(topicId, out Topic? topic);
        if (partialMessage.HasPartialMessage && topic?.RequestsPartialMessages is not true)
        {
            // SubOpts have no acknowledgement. A peer may still be acting on a prior request.
            if (topic?.HasRequestedPartialMessages is not true)
            {
                ApplyBehaviorPenalty(peerId, 1.0);
            }
            logger?.LogDebug("Ignoring unsolicited partial data from {peerId} for topic {topicId}", peerId, topicId);
            return;
        }

        if (topic?.IsSubscribed is not true || !topic.SupportsSendingPartialMessages)
        {
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

    private void HandleNewMessages(PeerId peerId, IEnumerable<Message> messages, ConcurrentDictionary<PeerId, Rpc> peerMessages,
        Dictionary<PeerId, Rpc> idontwantMessages, List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages,
        List<PendingValidation> deferredMessages)
    {
        // Check if peer is graylisted (Gossipsub v1.1)
        if (!IsDirectPeer(peerId) && ShouldGraylistPeer(peerId))
        {
            logger?.LogDebug("Ignoring messages from graylisted peer {peerId}", peerId);
            return;
        }

        if (logger?.IsEnabled(LogLevel.Trace) is true)
        {
            int knownMessages = messages.Select(_settings.GetMessageId).Count(messageId =>
                _pendingValidations.ContainsKey(messageId) || _limboMessageCache.Contains(messageId) || _seenMessages.Contains(messageId));
            logger?.LogTrace($"Messages received: {messages.Count()}, already known: {knownMessages}. All: {string.Join(",", messages.Select(_settings.GetMessageId))}.");
        }
        else
        {
            logger?.LogDebug($"Messages received: {messages.Count()}");
        }

        foreach (Message? message in messages)
        {
            MessageId messageId = _settings.GetMessageId(message);
            if (_pendingValidations.ContainsKey(messageId) || _limboMessageCache.Contains(messageId) || _seenMessages.Contains(messageId))
            {
                continue;
            }

            if (!message.VerifySignature(_settings.DefaultSignaturePolicy))
            {
                // An unauthenticated copy can share the application message ID of a valid message.
                // It also cannot fulfill an IWANT promise.
                RecordMessageDelivery(peerId, message, message.Topic, false);
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
                    _limboMessageCache.Add(messageId);
                    _iwantPromises.Fulfill(messageId);
                    RecordMessageDelivery(peerId, validated, validated.Topic, false);
                    continue;
                case MessageValidity.Ignored:
                    _limboMessageCache.Add(messageId);
                    _iwantPromises.Fulfill(messageId);
                    continue;
                case MessageValidity.Throttled:
                    _iwantPromises.Clear(peerId);
                    continue;
            }

            if (validity == MessageValidity.Deferred)
            {
                // The peer delivered an authenticated message, even if local capacity drops it.
                _iwantPromises.Fulfill(messageId);
                if (OnDeferredMessage is null)
                {
                    logger?.LogDebug("Dropping deferred pubsub message for topic {topic}: no deferred validator is registered", validated.Topic);
                    continue;
                }

                int size = validated.CalculateSize();
                // Local saturation can affect an honest peer, so it is not scored as invalid delivery.
                if (_pendingValidations.Count >= _settings.MaxPendingValidationMessages)
                {
                    logger?.LogDebug("Dropping deferred pubsub message for topic {topic} from {peerId}: pending validation count limit reached", validated.Topic, peerId);
                    continue;
                }
                if (size > _settings.MaxPendingValidationBytes - _pendingValidationBytes)
                {
                    logger?.LogDebug("Dropping deferred pubsub message for topic {topic} from {peerId}: pending validation byte limit reached", validated.Topic, peerId);
                    continue;
                }

                if (!TryGetPendingValidationExpiry(_timeProvider.GetUtcNow(), out DateTimeOffset expiresAt))
                {
                    logger?.LogDebug("Dropping deferred pubsub message for topic {topic}: pending validation timeout is invalid", validated.Topic);
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

            AcceptMessage(messageId, validated, peerId, peerMessages, idontwantMessages, receivedMessages);
        }
    }

    private void AcceptMessage(MessageId messageId, Message message, PeerId peerId, ConcurrentDictionary<PeerId, Rpc> peerMessages,
        Dictionary<PeerId, Rpc> idontwantMessages, List<(string Topic, PeerId PeerId, byte[] Data)> receivedMessages)
    {
        _seenMessages.Add(messageId);
        _messageCache.Put(messageId, message);
        _iwantPromises.Fulfill(messageId);
        RecordMessageDelivery(peerId, message, message.Topic, true);
        AddIdontwantMessages(message, messageId, peerId, idontwantMessages);

        PeerId author = new(message.From.ToArray());
        receivedMessages.Add((message.Topic, peerId, message.Data.ToByteArray()));

        foreach (PeerId directPeerId in GetDirectPeersForTopic(message.Topic))
        {
            if (directPeerId != author && directPeerId != peerId && ShouldSendFullMessage(directPeerId, message.Topic, messageId))
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
                if (ShouldSendFullMessage(peer, message.Topic, messageId))
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
                if (GetPeerScore(peer) >= _settings.PublishThreshold && ShouldSendFullMessage(peer, message.Topic, messageId))
                {
                    peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                }
            }
        }
    }
    private void AddIdontwantMessages(Message message, MessageId messageId, PeerId? source,
        Dictionary<PeerId, Rpc> idontwantMessages)
    {
        // Do not amplify a message ID across the mesh.
        if (!mesh.TryGetValue(message.Topic, out HashSet<PeerId>? meshPeers) ||
            message.Data.Length < _settings.IdontwantMessageThreshold ||
            message.Data.Length < (long)meshPeers.Count * (messageId.Bytes.Length + 16))
        {
            return;
        }

        ByteString idBytes = ByteString.CopyFrom(messageId.Bytes);
        bool requestsPartialMessages = topicState.TryGetValue(message.Topic, out Topic? topic) && topic.RequestsPartialMessages;
        foreach (PeerId meshPeerId in meshPeers)
        {
            if (meshPeerId == source ||
                !peerState.TryGetValue(meshPeerId, out PubsubPeer? meshPeer) ||
                meshPeer.Protocol < PubsubPeer.PubsubProtocol.GossipsubV12 ||
                (requestsPartialMessages && meshPeer.SupportsSendingPartialMessages(message.Topic)))
            {
                continue;
            }

            if (idontwantMessages.TryGetValue(meshPeerId, out Rpc? idontwant) &&
                idontwant.Control.Idontwant[^1].MessageIDs.Count < _settings.MaxIdontwantLength)
            {
                idontwant.Control.Idontwant[^1].MessageIDs.Add(idBytes);
                continue;
            }

            if (!meshPeer.Control.TrySendIdontwant(_settings.MaxIdontwantMessages))
            {
                continue;
            }

            if (idontwant is null)
            {
                idontwant = new Rpc { Control = new ControlMessage() };
                idontwantMessages.Add(meshPeerId, idontwant);
            }
            idontwant.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { idBytes } });
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
        // A single RPC can batch one IHAVE envelope per topic.
        if (!control.TryAcceptIHave(_settings.MaxIHaveMessages) || control.IHaveRequested >= _settings.MaxIHaveLength)
        {
            return;
        }

        HashSet<MessageId> messageIds = [];
        foreach (ControlIHave ihave in ihaves)
        {
            if (!mesh.ContainsKey(ihave.TopicID))
            {
                continue;
            }

            foreach (ByteString idBytes in ihave.MessageIDs.Take(_settings.MaxIHaveLength))
            {
                MessageId messageId = new(idBytes.ToByteArray());
                if (!_pendingValidations.ContainsKey(messageId) &&
                    !_seenMessages.Contains(messageId) && !_limboMessageCache.Contains(messageId))
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

    private bool IsUnwantedBy(PeerId peerId, MessageId messageId) =>
        peerState.TryGetValue(peerId, out PubsubPeer? peer) && peer.Control.IsUnwanted(messageId);

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
