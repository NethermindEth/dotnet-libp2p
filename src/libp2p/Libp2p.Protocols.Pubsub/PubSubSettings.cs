// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public class PubsubSettings
{
    public static PubsubSettings Default { get; } = new();

    public int ReconnectionAttempts { get; set; } = 10;
    public int ReconnectionPeriod { get; set; } = 15_000;

    public int Degree { get; set; } = 6; // The desired outbound degree of the network 	6
    public int LowestDegree { get; set; } = 4; // Lower bound for outbound degree 	4
    public int HighestDegree { get; set; } = 12; // Upper bound for outbound degree 	12
    public int LazyDegree { get; set; } = 6; // (Optional) the outbound degree for gossip emission D

    public int MaxConnections { get; set; }

    public int HeartbeatInterval { get; set; } = 1_000; // Time between heartbeats 	1 second
    public int FanoutTtl { get; set; } = 60 * 1000; // Time-to-live for each topic's fanout state 	60 seconds
    public int mcache_len { get; set; } = 5; // Number of history windows in message cache 	5
    public int mcache_gossip { get; set; } = 3; // Number of history windows to use when emitting gossip 	3
    public int MessageCacheTtl { get; set; } = 2 * 60 * 1000; // Expiry time for cache of seen message ids 	2 minutes
    public SignaturePolicy DefaultSignaturePolicy { get; set; } = SignaturePolicy.StrictSign;
    public int MaxIdontwantMessages { get; set; } = 50;

    public Func<Message, MessageId> GetMessageId { get; set; } = ConcatFromAndSeqno;

    public enum SignaturePolicy
    {
        StrictSign,
        StrictNoSign,
    }

    // ==================== Gossipsub v1.1 Peer Scoring Parameters ====================

    // Global Scoring Thresholds
    public double GossipThreshold { get; set; } = -10;
    public double PublishThreshold { get; set; } = -50;
    public double GraylistThreshold { get; set; } = -100;
    public double AcceptPXThreshold { get; set; } = 100;
    public double OpportunisticGraftThreshold { get; set; } = 5;

    // Score Decay
    public int DecayInterval { get; set; } = 1000; // 1 second
    public double DecayToZero { get; set; } = 0.01;
    public int RetainScore { get; set; } = 10 * 60 * 1000; // 10 minutes

    // Mesh Management
    public int DScore { get; set; } = 5;
    public int DOut { get; set; } = 2;
    public int PruneBackoff { get; set; } = 60 * 1000; // 1 minute
    public int UnsubscribeBackoff { get; set; } = 10 * 1000; // 10 seconds
    public bool FloodPublish { get; set; } = true;
    public double GossipFactor { get; set; } = 0.25;

    // Global Peer Scoring Parameters (P5, P6, P7)
    public double AppSpecificWeight { get; set; } = 10;
    public double IPColocationFactorWeight { get; set; } = -35;
    public double IPColocationFactorThreshold { get; set; } = 10;
    public double BehaviourPenaltyWeight { get; set; } = -15;
    public double BehaviourPenaltyDecay { get; set; } = 0.99;

    // Topic Scoring Parameters (P1, P2, P3, P3b, P4)
    // These can be configured per-topic using the TopicScoreParams class
    public Dictionary<string, TopicScoreParams> TopicScoreParams { get; set; } = new();

    public double TopicScoreCap { get; set; } = 3600;

    public static MessageId ConcatFromAndSeqno(Message message) => new([.. message.From, .. message.Seqno]);
}

/// <summary>
/// Topic-specific peer scoring parameters for Gossipsub v1.1
/// </summary>
public class TopicScoreParams
{
    public double TopicWeight { get; set; } = 0.5;

    // P1: Time in Mesh
    public double TimeInMeshWeight { get; set; } = 0.0027;
    public int TimeInMeshQuantum { get; set; } = 1000; // 1 second
    public double TimeInMeshCap { get; set; } = 3600;

    // P2: First Message Deliveries
    public double FirstMessageDeliveriesWeight { get; set; } = 1;
    public double FirstMessageDeliveriesDecay { get; set; } = 0.99;
    public double FirstMessageDeliveriesCap { get; set; } = 2000;

    // P3: Mesh Message Deliveries
    public double MeshMessageDeliveriesWeight { get; set; } = -1;
    public double MeshMessageDeliveriesDecay { get; set; } = 0.97;
    public double MeshMessageDeliveriesThreshold { get; set; } = 20;
    public double MeshMessageDeliveriesCap { get; set; } = 100;
    public int MeshMessageDeliveriesActivation { get; set; } = 5000; // 5 seconds
    public int MeshMessageDeliveryWindow { get; set; } = 10; // 10ms

    // P3b: Mesh Message Delivery Failures
    public double MeshFailurePenaltyWeight { get; set; } = -1;
    public double MeshFailurePenaltyDecay { get; set; } = 0.997;

    // P4: Invalid Messages
    public double InvalidMessageDeliveriesWeight { get; set; } = -1;
    public double InvalidMessageDeliveriesDecay { get; set; } = 0.99;
}
