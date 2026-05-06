// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Scoring-related methods for PubsubRouter (Gossipsub v1.1)
/// </summary>
public partial class PubsubRouter
{
    private DateTime _lastScoreDecay = DateTime.UtcNow;
    private readonly ConcurrentDictionary<MessageId, (PeerId FirstDeliverer, DateTime FirstDeliveryTime)> _firstDeliveryTracker = new();

    /// <summary>
    /// Gets the score for a peer
    /// </summary>
    private double GetPeerScore(PeerId peerId)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return 0;
        }

        // Count IPs for P6 calculation
        var ipCounts = new Dictionary<string, int>();
        foreach (var p in peerState.Values)
        {
            if (p.Score.IPAddress != null)
            {
                ipCounts.TryGetValue(p.Score.IPAddress, out int count);
                ipCounts[p.Score.IPAddress] = count + 1;
            }
        }

        return peer.Score.GetScore(ipCounts);
    }

    /// <summary>
    /// Applies periodic decay to peer scores
    /// </summary>
    private void DecayScores()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastScoreDecay).TotalMilliseconds < _settings.DecayInterval)
        {
            return;
        }

        _lastScoreDecay = now;

        foreach (var peer in peerState.Values)
        {
            peer.Score.Decay();
        }

        // Clean up old first delivery tracking
        var cutoff = now.AddMilliseconds(-_settings.MessageCacheTtl);
        var toRemove = _firstDeliveryTracker.Where(kvp => kvp.Value.FirstDeliveryTime < cutoff)
            .Select(kvp => kvp.Key).ToList();
        foreach (var key in toRemove)
        {
            _firstDeliveryTracker.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Records a message delivery for scoring purposes
    /// </summary>
    private void RecordMessageDelivery(PeerId peerId, Message message, string topic, bool isValid)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        MessageId messageId = _settings.GetMessageId(message);

        if (!isValid)
        {
            // P4: Invalid message
            var topicScore = peer.Score.GetTopicScore(topic);
            topicScore.RecordInvalidMessage();
            logger?.LogDebug("Peer {peerId} delivered invalid message, score updated", peerId);
            return;
        }

        // Check if this is the first delivery
        bool isFirstDelivery = _firstDeliveryTracker.TryAdd(messageId, (peerId, DateTime.UtcNow));

        if (isFirstDelivery)
        {
            // P2: First message delivery
            var topicScore = peer.Score.GetTopicScore(topic);
            topicScore.RecordFirstMessageDelivery();
            logger?.LogTrace("Peer {peerId} first delivered message {messageId}", peerId, messageId);
        }

        // Check if peer is in mesh for P3
        if (mesh.TryGetValue(topic, out HashSet<PeerId>? meshPeers) && meshPeers.Contains(peerId))
        {
            var topicScore = peer.Score.GetTopicScore(topic);

            // Check if this is near-first delivery
            if (isFirstDelivery || topicScore.IsNearFirstDelivery(messageId))
            {
                // P3: Mesh message delivery
                topicScore.RecordMeshMessageDelivery(messageId);
                logger?.LogTrace("Peer {peerId} mesh delivered message {messageId}", peerId, messageId);
            }
        }
    }

    /// <summary>
    /// Records when a peer joins the mesh (for P1)
    /// </summary>
    private void RecordPeerJoinMesh(PeerId peerId, string topic)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        var topicScore = peer.Score.GetTopicScore(topic);
        topicScore.JoinMesh();
        logger?.LogTrace("Peer {peerId} joined mesh for topic {topic}", peerId, topic);
    }

    /// <summary>
    /// Records when a peer leaves the mesh (for P1 and P3b)
    /// </summary>
    private void RecordPeerLeaveMesh(PeerId peerId, string topic)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        var topicScore = peer.Score.GetTopicScore(topic);
        topicScore.LeaveMesh();
        logger?.LogTrace("Peer {peerId} left mesh for topic {topic}", peerId, topic);
    }

    /// <summary>
    /// Applies a behavioral penalty to a peer (P7)
    /// </summary>
    private void ApplyBehaviorPenalty(PeerId peerId, double penalty)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            return;
        }

        peer.Score.AddPenalty(penalty);
        logger?.LogDebug("Applied behavioral penalty {penalty} to peer {peerId}", penalty, peerId);
    }

    /// <summary>
    /// Checks if peer score is above threshold
    /// </summary>
    private bool IsPeerAboveThreshold(PeerId peerId, double threshold)
    {
        double score = GetPeerScore(peerId);
        return score >= threshold;
    }

    /// <summary>
    /// Gets peers above a score threshold
    /// </summary>
    private IEnumerable<PeerId> GetPeersAboveThreshold(IEnumerable<PeerId> peers, double threshold)
    {
        return peers.Where(p => IsPeerAboveThreshold(p, threshold));
    }

    /// <summary>
    /// Gets the best scoring peers from a collection
    /// </summary>
    private List<PeerId> GetBestScoringPeers(IEnumerable<PeerId> peers, int count)
    {
        return peers
            .OrderByDescending(p => GetPeerScore(p))
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Checks if peer should be graylisted based on score
    /// </summary>
    private bool ShouldGraylistPeer(PeerId peerId)
    {
        double score = GetPeerScore(peerId);
        return score < _settings.GraylistThreshold;
    }

    /// <summary>
    /// Gets median score of peers in mesh for a topic
    /// </summary>
    private double GetMedianMeshScore(string topic)
    {
        if (!mesh.TryGetValue(topic, out HashSet<PeerId>? meshPeers) || meshPeers.Count == 0)
        {
            return 0;
        }

        var scores = meshPeers.Select(p => GetPeerScore(p)).OrderBy(s => s).ToList();
        int count = scores.Count;

        if (count == 0)
            return 0;

        if (count % 2 == 0)
        {
            return (scores[count / 2 - 1] + scores[count / 2]) / 2.0;
        }
        else
        {
            return scores[count / 2];
        }
    }

    /// <summary>
    /// Sets the application-specific score for a peer (P5)
    /// </summary>
    public void SetAppSpecificScore(PeerId peerId, double score)
    {
        if (peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            peer.Score.AppSpecificScore = score;
            logger?.LogDebug("Set app-specific score for {peerId} to {score}", peerId, score);
        }
    }

    /// <summary>
    /// Sets IP address for IP colocation tracking (P6)
    /// </summary>
    private void SetPeerIPAddress(PeerId peerId, string ipAddress)
    {
        if (peerState.TryGetValue(peerId, out PubsubPeer? peer))
        {
            peer.Score.IPAddress = ipAddress;
        }
    }
}
