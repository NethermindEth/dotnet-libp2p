// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public class PubsubSettings
{
    public static PubsubSettings Default { get; } = new();

    // Connection and Reconnection Settings
    public int ReconnectionAttempts { get; set; } = 10;
    public int ReconnectionPeriod { get; set; } = 15_000;
    public int MaxConnections { get; set; }

    // Network Topology Settings
    /// <summary>
    /// The desired outbound degree of the network
    /// </summary>
    public int Degree { get; set; } = 6;

    /// <summary>
    /// Lower bound for outbound degree
    /// </summary>
    public int LowestDegree { get; set; } = 4;

    /// <summary>
    /// Upper bound for outbound degree
    /// </summary>
    public int HighestDegree { get; set; } = 12;

    /// <summary>
    /// (Optional) the outbound degree for gossip emission, equal to <see cref="Degree"/> by default
    /// </summary>
    public int LazyDegree { get => lazyDegree ?? Degree; set => lazyDegree = value; }
    private int? lazyDegree = null;

    // Timing Settings
    /// <summary>
    /// Time between heartbeats (in milliseconds)
    /// </summary>
    public int HeartbeatInterval { get; set; } = 1_000;

    /// <summary>
    /// Time-to-live for each topic's fanout state (in milliseconds)
    /// </summary>
    public int FanoutTtl { get; set; } = 60 * 1000;

    /// <summary>
    /// Expiry time for cache of seen message ids (in milliseconds)
    /// </summary>
    public int SeenTtl { get; set; } = 2 * 60 * 1000;

    public int PruneBackoff { get; set; } = 1 * 60 * 1000;
    public int UnsubscribeBackoff { get; set; } = 10 * 1000;

    // Message Cache Settings
    /// <summary>
    /// Number of history windows in message cache
    /// </summary>
    public int MessageCacheLen { get; set; } = 5;

    /// <summary>
    /// Number of history windows to use when emitting gossip
    /// </summary>
    public int MessageCacheGossip { get; set; } = 3;

    /// <summary>
    /// Message cache TTL (in milliseconds)
    /// </summary>
    public int MessageCacheTtl { get; set; } = 2 * 60 * 1000;

    // Publishing and Gossip Settings
    public bool FloodPublish { get; set; } = true;
    public double GossipFactor { get; set; } = 0.25;
    public int DScore { get; set; } = 5;
    public int DOut { get; set; } = 2;
    public int MaxIdontwantMessages { get; set; } = 50;

    // Message Handling
    /// <summary>
    /// Message id generator, uses From and SeqNo concatenation by default
    /// </summary>
    public Func<Message, MessageId> GetMessageId { get; set; } = ConcatFromAndSeqno;

    public SignaturePolicy DefaultSignaturePolicy { get; set; } = SignaturePolicy.StrictSign;

    // Peer Scoring Thresholds
    public double GossipThreshold { get; set; } = 0;
    public double PublishThreshold { get; set; } = 0;
    public double GraylistThreshold { get; set; } = 0;
    public double AcceptPXThreshold { get; set; } = 0;
    public double OpportunisticGraftThreshold { get; set; } = 0;

    // Score Decay Settings
    public double DecayInterval { get; set; } = 1;
    public double DecayToZero { get; set; } = 1;
    public double RetainScore { get; set; } = 1;

    // Global Peer Scoring Parameters
    public double AppSpecificWeight { get; set; } = 1;
    public double IpColocationFactorWeight { get; set; } = 1;
    public double IpColocationFactorThreshold { get; set; } = 1;
    public double BehaviourPenaltyWeight { get; set; } = 1;
    public double BehaviourPenaltyDecay { get; set; } = 1;
    public double TopicWeight { get; set; } = 1;

    // P1: Time in Mesh
    public double TimeInMeshWeight { get; set; } = 1;
    public double TimeInMeshQuantum { get; set; } = 1;
    public double TimeInMeshCap { get; set; } = 1;

    // P2: First Message Deliveries
    public double FirstMessageDeliveriesWeight { get; set; } = 1;
    public double FirstMessageDeliveriesDecay { get; set; } = 1;
    public double FirstMessageDeliveriesCap { get; set; } = 1;

    // P3: Mesh Message Deliveries
    public double MeshMessageDeliveriesWeight { get; set; } = 1;
    public double MeshMessageDeliveriesDecay { get; set; } = 1;
    public double MeshMessageDeliveriesThreshold { get; set; } = 1;
    public double MeshMessageDeliveriesCap { get; set; } = 1;
    public double MeshMessageDeliveriesActivation { get; set; } = 1;
    public double MeshMessageDeliveryWindow { get; set; } = 1;

    // P3b: Mesh Failure Penalty
    public double MeshFailurePenaltyWeight { get; set; } = 1;
    public double MeshFailurePenaltyDecay { get; set; } = 1;

    // P4: Invalid Message Deliveries
    public double InvalidMessageDeliveriesWeight { get; set; } = 1;
    public double InvalidMessageDeliveriesDecay { get; set; } = 1;

    public enum SignaturePolicy
    {
        StrictSign,
        StrictNoSign,
    }

    public static MessageId ConcatFromAndSeqno(Message message) => new([.. message.From, .. message.Seqno]);
}
