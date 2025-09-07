// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Represents the scoring state for a single peer
/// </summary>
public class PeerState
{
    public PeerId PeerId { get; }
    public DateTime ConnectedAt { get; }
    public DateTime LastScoreUpdate { get; set; }

    public double Score { get; set; } = 0.0;

    /// <summary>
    /// Per-topic scoring parameters (P1-P4) mapped from a string to a TopicState (Defined below)
    /// </summary>
    public Dictionary<string, TopicState> TopicStates { get; } = new();

    public GlobalScoreState GlobalState { get; } = new();

    public PeerState(PeerId peerId)
    {
        PeerId = peerId;
        ConnectedAt = DateTime.UtcNow;
        LastScoreUpdate = DateTime.UtcNow;
    }

    /// <summary>
    ///  TopicState for a given topic
    /// </summary>
    public TopicState GetOrCreateTopicState(string topic)
    {
        if (!TopicStates.TryGetValue(topic, out var topicState))
        {
            topicState = new TopicState(topic);
            TopicStates[topic] = topicState;
        }
        return topicState;
    }
}

public class TopicState
{
    public string Topic { get; }
    public DateTime CreatedAt { get; }

    // P1: Time in Mesh
    public DateTime? MeshJoinedAt { get; set; }
    public double TimeInMesh { get; set; } = 0.0;

    // P2: First Message Deliveries
    public double FirstMessageDeliveries { get; set; } = 0.0;

    // P3: Mesh Message Delivery Rate
    public double MeshMessageDeliveries { get; set; } = 0.0;
    public double MeshMessageDeliveryDeficit { get; set; } = 0.0;

    // P3b: Mesh Message Delivery Failures
    public double MeshFailure { get; set; } = 0.0;

    // P4: Invalid Messages
    public double InvalidMessageDeliveries { get; set; } = 0.0;

    public TopicState(string topic)
    {
        Topic = topic;
        CreatedAt = DateTime.UtcNow;
    }
}

public class GlobalScoreState
{
    // P5: Application-Specific Score
    public double AppSpecificScore { get; set; } = 0.0;

    // P6: IP Colocation Factor
    public double IpColocationFactor { get; set; } = 0.0;

    // P7: Behavioral Penalty
    public double BehaviorPenaltyCounter { get; set; } = 0.0;
    public double BehaviorPenalty { get; set; } = 0.0;
}
