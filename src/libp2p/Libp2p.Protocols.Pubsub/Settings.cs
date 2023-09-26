// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public class Settings
{
    public static Settings Default => new();

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
    public int HighestDegree { get; set; } = 12;// 	12

    /// <summary>
    /// (Optional) the outbound degree for gossip emission, ueqaul to <see cref="Degree"/> by default
    /// </summary>
    public int LazyDegree { get => lazyDegree ?? Degree; set => lazyDegree = value; }

    private int? lazyDegree = null;

    /// <summary>
    /// Time between heartbeats
    /// </summary>
    public int HeartbeatIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Time-to-live for each topic's fanout state
    /// </summary>
    public int FanoutTtlMs { get; set; } = 60 * 1000;

    /// <summary>
    /// Number of history windows in message cache
    /// </summary>
    public int MessageCacheLen { get; set; } = 5;

    /// <summary>
    /// Number of history windows to use when emitting gossip
    /// </summary>
    public int MessageCacheGossip { get; set; } = 3;

    /// <summary>
    /// Expiry time for cache of seen message ids
    /// </summary>
    public int SeenTtlMs { get; set; } = 2 * 60 * 1000;

    /// <summary>
    /// Message id generator, uses From and SeqNo contacatenation by default
    /// </summary>
    public Func<Message, MessageId> GetMessageId = GetMessageIdFromSenderAndSeqNo;

    private static MessageId GetMessageIdFromSenderAndSeqNo(Message message)
    {
        return new(message.From.Concat(message.Seqno).ToArray());
    }

    public int PruneBackoffMs { get; set; } = 1 * 60 * 1000;
    public int UnsubscribeBackoffMs { get; set; } = 10 * 1000;
    public bool FloodPublish { get; set; } = true;
    public double GossipFactor { get; set; } = 0.25;
    public int DScore { get; set; } = 5;
    public int DOut { get; set; } = 2;

    public double GossipThreshold { get; set; } = -0;
    public double PublishThreshold { get; set; } = -0;
    public double GraylistThreshold { get; set; } = -0;
    public double AcceptPXThreshold { get; set; } = 0;
    public double OpportunisticGraftThreshold { get; set; } = 0;
    public double DecayInterval { get; set; } = 1;
    public double DecayToZero { get; set; } = 1;
    public double RetainScore { get; set; } = 1;

    public double AppSpecificWeight { get; set; } = 1;
    public double IpColocationFactorWeight { get; set; } = 1;
    public double IpColocationFactorThreshold { get; set; } = 1;
    public double BehaviourPenaltyWeight { get; set; } = 1;
    public double BehaviourPenaltyDecay { get; set; } = 1;

    public double TopicWeight { get; set; } = 1;

    // P1

    public double TimeInMeshWeight { get; set; } = 1;
    public double TimeInMeshQuantum { get; set; } = 1;
    public double TimeInMeshCap { get; set; } = 1;

    // P2

    public double FirstMessageDeliveriesWeight { get; set; } = 1;
    public double FirstMessageDeliveriesDecay { get; set; } = 1;
    public double FirstMessageDeliveriesCap { get; set; } = 1;

    // P3

    public double MeshMessageDeliveriesWeight { get; set; } = 1;
    public double MeshMessageDeliveriesDecay { get; set; } = 1;
    public double MeshMessageDeliveriesThreshold { get; set; } = 1;
    public double MeshMessageDeliveriesCap { get; set; } = 1;
    public double MeshMessageDeliveriesActivation { get; set; } = 1;
    public double MeshMessageDeliveryWindow { get; set; } = 1;

    // P3b

    public double MeshFailurePenaltyWeight { get; set; } = 1;
    public double MeshFailurePenaltyDecay { get; set; } = 1;


    // P4

    public double InvalidMessageDeliveriesWeight { get; set; } = 1;
    public double InvalidMessageDeliveriesDecay { get; set; } = 1;
}
