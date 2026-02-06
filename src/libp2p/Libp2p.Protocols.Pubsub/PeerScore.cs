// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Tracks scoring state for a single peer across all topics
/// </summary>
public class PeerScore
{
    private readonly PubsubSettings _settings;
    private readonly ConcurrentDictionary<string, TopicScore> _topicScores = new();

    // Global parameters
    public double AppSpecificScore { get; set; } = 0;
    public double BehaviourPenalty { get; set; } = 0;

    // IP colocation tracking (managed at router level)
    public string? IPAddress { get; set; }

    // Last decay time
    public DateTime LastDecay { get; private set; } = DateTime.UtcNow;

    public PeerScore(PubsubSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Gets or creates the topic score for a given topic
    /// </summary>
    public TopicScore GetTopicScore(string topic)
    {
        return _topicScores.GetOrAdd(topic, _ => new TopicScore(_settings.TopicScoreParams.GetValueOrDefault(topic) ?? new TopicScoreParams()));
    }

    /// <summary>
    /// Calculates the total score for this peer
    /// </summary>
    public double GetScore(Dictionary<string, int> ipCounts)
    {
        double score = 0;

        // Topic scores (P1, P2, P3, P3b, P4)
        double topicScore = 0;
        foreach (var kvp in _topicScores)
        {
            string topic = kvp.Key;
            TopicScore ts = kvp.Value;

            var topicParams = _settings.TopicScoreParams.GetValueOrDefault(topic);
            if (topicParams == null)
                continue;

            double topicContribution =
                topicParams.TopicWeight * ts.CalculateScore(topicParams);

            topicScore += topicContribution;
        }

        // Apply topic cap
        if (_settings.TopicScoreCap > 0 && topicScore > _settings.TopicScoreCap)
        {
            topicScore = _settings.TopicScoreCap;
        }

        score += topicScore;

        // P5: App specific score
        score += _settings.AppSpecificWeight * AppSpecificScore;

        // P6: IP colocation factor
        if (IPAddress != null && ipCounts.TryGetValue(IPAddress, out int ipCount))
        {
            if (ipCount > _settings.IPColocationFactorThreshold)
            {
                double surplus = ipCount - _settings.IPColocationFactorThreshold;
                score += _settings.IPColocationFactorWeight * surplus * surplus;
            }
        }

        // P7: Behaviour penalty
        score += _settings.BehaviourPenaltyWeight * BehaviourPenalty * BehaviourPenalty;

        return score;
    }

    /// <summary>
    /// Applies decay to all score parameters
    /// </summary>
    public void Decay()
    {
        DateTime now = DateTime.UtcNow;
        LastDecay = now;

        // Decay topic scores
        foreach (var ts in _topicScores.Values)
        {
            ts.Decay(_settings.DecayToZero);
        }

        // Decay behaviour penalty
        BehaviourPenalty *= _settings.BehaviourPenaltyDecay;
        if (BehaviourPenalty < _settings.DecayToZero)
        {
            BehaviourPenalty = 0;
        }
    }

    /// <summary>
    /// Adds a behaviour penalty
    /// </summary>
    public void AddPenalty(double penalty)
    {
        BehaviourPenalty += penalty;
    }

    /// <summary>
    /// Removes topic score when peer leaves mesh
    /// </summary>
    public void RemoveTopicScore(string topic)
    {
        _topicScores.TryRemove(topic, out _);
    }
}

/// <summary>
/// Tracks scoring state for a peer within a specific topic
/// </summary>
public class TopicScore
{
    private readonly TopicScoreParams _params;

    // P1: Time in mesh
    public DateTime? MeshJoinTime { get; set; }
    public TimeSpan AccumulatedMeshTime { get; private set; } = TimeSpan.Zero;

    // P2: First message deliveries
    public double FirstMessageDeliveries { get; set; } = 0;

    // P3: Mesh message deliveries
    public double MeshMessageDeliveries { get; set; } = 0;
    public Dictionary<MessageId, DateTime> RecentFirstDeliveries { get; } = new();

    // P3b: Mesh failure penalty
    public double MeshFailurePenalty { get; set; } = 0;

    // P4: Invalid message deliveries
    public double InvalidMessageDeliveries { get; set; } = 0;

    public TopicScore(TopicScoreParams parameters)
    {
        _params = parameters;
    }

    /// <summary>
    /// Records when peer joins the mesh
    /// </summary>
    public void JoinMesh()
    {
        MeshJoinTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Records when peer leaves the mesh and calculates deficit penalty
    /// </summary>
    public void LeaveMesh()
    {
        if (MeshJoinTime.HasValue)
        {
            AccumulatedMeshTime += DateTime.UtcNow - MeshJoinTime.Value;

            // Check for message delivery deficit and apply P3b penalty
            if (AccumulatedMeshTime.TotalMilliseconds > _params.MeshMessageDeliveriesActivation)
            {
                if (MeshMessageDeliveries < _params.MeshMessageDeliveriesThreshold)
                {
                    double deficit = _params.MeshMessageDeliveriesThreshold - MeshMessageDeliveries;
                    MeshFailurePenalty += deficit * deficit;
                }
            }

            MeshJoinTime = null;
        }
    }

    /// <summary>
    /// Gets the current time in mesh
    /// </summary>
    public TimeSpan GetTimeInMesh()
    {
        if (MeshJoinTime.HasValue)
        {
            return AccumulatedMeshTime + (DateTime.UtcNow - MeshJoinTime.Value);
        }
        return AccumulatedMeshTime;
    }

    /// <summary>
    /// Records a first message delivery
    /// </summary>
    public void RecordFirstMessageDelivery()
    {
        FirstMessageDeliveries += 1;
        if (FirstMessageDeliveries > _params.FirstMessageDeliveriesCap)
        {
            FirstMessageDeliveries = _params.FirstMessageDeliveriesCap;
        }
    }

    /// <summary>
    /// Records a mesh message delivery (first or near-first from a mesh peer)
    /// </summary>
    public void RecordMeshMessageDelivery(MessageId messageId)
    {
        RecentFirstDeliveries[messageId] = DateTime.UtcNow;

        MeshMessageDeliveries += 1;
        if (MeshMessageDeliveries > _params.MeshMessageDeliveriesCap)
        {
            MeshMessageDeliveries = _params.MeshMessageDeliveriesCap;
        }

        // Clean up old deliveries
        var cutoff = DateTime.UtcNow.AddMilliseconds(-_params.MeshMessageDeliveryWindow * 10);
        var toRemove = RecentFirstDeliveries.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in toRemove)
        {
            RecentFirstDeliveries.Remove(key);
        }
    }

    /// <summary>
    /// Checks if a message was recently delivered (for near-first delivery detection)
    /// </summary>
    public bool IsNearFirstDelivery(MessageId messageId)
    {
        if (RecentFirstDeliveries.TryGetValue(messageId, out DateTime deliveryTime))
        {
            return (DateTime.UtcNow - deliveryTime).TotalMilliseconds <= _params.MeshMessageDeliveryWindow;
        }
        return false;
    }

    /// <summary>
    /// Records an invalid message
    /// </summary>
    public void RecordInvalidMessage()
    {
        InvalidMessageDeliveries += 1;
    }

    /// <summary>
    /// Calculates the score contribution from this topic
    /// </summary>
    public double CalculateScore(TopicScoreParams parameters)
    {
        double score = 0;

        // P1: Time in mesh
        TimeSpan timeInMesh = GetTimeInMesh();
        double p1 = timeInMesh.TotalMilliseconds / parameters.TimeInMeshQuantum;
        if (p1 > parameters.TimeInMeshCap)
        {
            p1 = parameters.TimeInMeshCap;
        }
        score += parameters.TimeInMeshWeight * p1;

        // P2: First message deliveries
        score += parameters.FirstMessageDeliveriesWeight * FirstMessageDeliveries;

        // P3: Mesh message delivery rate
        double p3 = 0;
        if (timeInMesh.TotalMilliseconds > parameters.MeshMessageDeliveriesActivation)
        {
            if (MeshMessageDeliveries < parameters.MeshMessageDeliveriesThreshold)
            {
                double deficit = parameters.MeshMessageDeliveriesThreshold - MeshMessageDeliveries;
                p3 = deficit * deficit;
            }
        }
        score += parameters.MeshMessageDeliveriesWeight * p3;

        // P3b: Mesh failure penalty
        score += parameters.MeshFailurePenaltyWeight * MeshFailurePenalty;

        // P4: Invalid messages
        double p4 = InvalidMessageDeliveries * InvalidMessageDeliveries;
        score += parameters.InvalidMessageDeliveriesWeight * p4;

        return score;
    }

    /// <summary>
    /// Applies decay to all counters
    /// </summary>
    public void Decay(double decayToZero)
    {
        // P2 decay
        FirstMessageDeliveries *= _params.FirstMessageDeliveriesDecay;
        if (FirstMessageDeliveries < decayToZero)
        {
            FirstMessageDeliveries = 0;
        }

        // P3 decay
        MeshMessageDeliveries *= _params.MeshMessageDeliveriesDecay;
        if (MeshMessageDeliveries < decayToZero)
        {
            MeshMessageDeliveries = 0;
        }

        // P3b decay
        MeshFailurePenalty *= _params.MeshFailurePenaltyDecay;
        if (MeshFailurePenalty < decayToZero)
        {
            MeshFailurePenalty = 0;
        }

        // P4 decay
        InvalidMessageDeliveries *= _params.InvalidMessageDeliveriesDecay;
        if (InvalidMessageDeliveries < decayToZero)
        {
            InvalidMessageDeliveries = 0;
        }
    }
}
