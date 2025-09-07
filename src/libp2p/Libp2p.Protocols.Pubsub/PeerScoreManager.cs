// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Manages peer scoring calculations
/// </summary>
public class PeerScoreManager
{
    private readonly PubsubSettings _settings;
    private readonly ILogger? _logger;

    public PeerScoreManager(PubsubSettings settings, ILogger? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    public double CalculateScore(PeerState peerState)
    {
        double totalScore = 0.0;

        // Calculate topic-based scores
        foreach (var (topic, topicState) in peerState.TopicStates)
        {
            double topicScore = CalculateTopicScore(topicState);
            totalScore += _settings.TopicWeight * topicScore;
        }

        // ToDo : Write topic cap config
        if (_settings.TopicWeight > 0)
        {
        }

        // Add global parameters
        totalScore += _settings.AppSpecificWeight * peerState.GlobalState.AppSpecificScore;
        totalScore += _settings.IpColocationFactorWeight * peerState.GlobalState.IpColocationFactor;
        totalScore += _settings.BehaviourPenaltyWeight * peerState.GlobalState.BehaviorPenalty;

        peerState.Score = totalScore;
        peerState.LastScoreUpdate = DateTime.UtcNow;

        return totalScore;
    }

    private double CalculateTopicScore(TopicState topicState)
    {
        double score = 0.0;

        // P1: Time in Mesh
        score += _settings.TimeInMeshWeight * CalculateP1(topicState);

        // P2: First Message Deliveries
        score += _settings.FirstMessageDeliveriesWeight * Math.Min(topicState.FirstMessageDeliveries, _settings.FirstMessageDeliveriesCap);

        // P3: Mesh Message Delivery Rate
        score += _settings.MeshMessageDeliveriesWeight * CalculateP3(topicState);

        // P3b: Mesh Message Delivery Failures
        score += _settings.MeshFailurePenaltyWeight * topicState.MeshFailure;

        // P4: Invalid Messages
        score += _settings.InvalidMessageDeliveriesWeight * topicState.InvalidMessageDeliveries;

        return score;
    }

    private double CalculateP1(TopicState topicState)
    {
        if (topicState.MeshJoinedAt == null)
            return 0.0;

        var timeInMesh = DateTime.UtcNow - topicState.MeshJoinedAt.Value;
        double p1 = timeInMesh.TotalSeconds / _settings.TimeInMeshQuantum;
        return Math.Min(p1, _settings.TimeInMeshCap);
    }

    private double CalculateP3(TopicState topicState)
    {
        if (topicState.MeshMessageDeliveries >= _settings.MeshMessageDeliveriesThreshold)
        {
            return 0.0; // No penalty if above threshold
        }

        // Calculate Penalty
        double deficit = _settings.MeshMessageDeliveriesThreshold - topicState.MeshMessageDeliveries;
        return -(deficit * deficit); // Square of the deficit as penalty
    }

    public bool IsScoreAboveThreshold(PeerState peerState, double threshold)
    {
        return peerState.Score >= threshold;
    }

    /// <summary>
    /// Apply decay to all scoring parameters
    /// </summary>
    public void ApplyDecay(PeerState peerState)
    {
        var now = DateTime.UtcNow;
        var timeSinceLastUpdate = now - peerState.LastScoreUpdate;

        if (timeSinceLastUpdate.TotalSeconds < _settings.DecayInterval)
            return;

        // Apply decay to each topic
        foreach (var topicState in peerState.TopicStates.Values)
        {
            ApplyTopicDecay(topicState);
        }

        ApplyGlobalDecay(peerState.GlobalState);

        // Recalculate score after decay
        CalculateScore(peerState);
    }

    private void ApplyTopicDecay(TopicState topicState)
    {
        topicState.FirstMessageDeliveries *= _settings.FirstMessageDeliveriesDecay;

        topicState.MeshMessageDeliveries *= _settings.MeshMessageDeliveriesDecay;

        topicState.MeshFailure *= _settings.MeshFailurePenaltyDecay;

        topicState.InvalidMessageDeliveries *= _settings.InvalidMessageDeliveriesDecay;
    }

    private void ApplyGlobalDecay(GlobalScoreState globalState)
    {
        // P7: Behavioral Penalty decay
        globalState.BehaviorPenaltyCounter *= _settings.BehaviourPenaltyDecay;
        globalState.BehaviorPenalty = globalState.BehaviorPenaltyCounter * globalState.BehaviorPenaltyCounter;
    }

    public void OnPeerJoinedMesh(PeerState peerState, string topic)
    {
        var topicState = peerState.GetOrCreateTopicState(topic);
        topicState.MeshJoinedAt = DateTime.UtcNow;
        _logger?.LogDebug($"Peer {peerState.PeerId} joined mesh for topic {topic}");
    }

    public void OnPeerLeftMesh(PeerState peerState, string topic)
    {
        if (peerState.TopicStates.TryGetValue(topic, out var topicState))
        {
            topicState.MeshJoinedAt = null;
            _logger?.LogDebug($"Peer {peerState.PeerId} left mesh for topic {topic}");
        }
    }

    public void OnFirstMessageDelivery(PeerState peerState, string topic)
    {
        var topicState = peerState.GetOrCreateTopicState(topic);
        topicState.FirstMessageDeliveries = Math.Min(topicState.FirstMessageDeliveries + 1.0, _settings.FirstMessageDeliveriesCap);
    }

    public void OnInvalidMessage(PeerState peerState, string topic)
    {
        var topicState = peerState.GetOrCreateTopicState(topic);
        topicState.InvalidMessageDeliveries += 1.0;
        _logger?.LogWarning($"Invalid message from peer {peerState.PeerId} in topic {topic}");
    }

    public void ApplyBehavioralPenalty(PeerState peerState, double penalty = 1.0)
    {
        peerState.GlobalState.BehaviorPenaltyCounter += penalty;
        peerState.GlobalState.BehaviorPenalty = peerState.GlobalState.BehaviorPenaltyCounter * peerState.GlobalState.BehaviorPenaltyCounter;

        _logger?.LogWarning($"Applied behavioral penalty {penalty} to peer {peerState.PeerId}");
    }
}
