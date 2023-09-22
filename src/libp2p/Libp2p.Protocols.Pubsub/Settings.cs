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
    public int LazyDegree { get => lazyDegree ?? Degree;  set => lazyDegree = value;  }

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
    public int McacheLen { get; set; } = 5;

    /// <summary>
    /// Number of history windows to use when emitting gossip
    /// </summary>
    public int McacheGossip { get; set; } = 3;

    /// <summary>
    /// Expiry time for cache of seen message ids
    /// </summary>
    public int MessageCacheTtlMs { get; set; } = 2 * 60 * 1000;

    /// <summary>
    /// Message id generator, uses From and SeqNo contacatenation by default
    /// </summary>
    public Func<Message, MessageId> GetMessageId = GetMessageIdFromSenderAndSeqNo;

    private static MessageId GetMessageIdFromSenderAndSeqNo(Message message)
    {
        return new(message.From.Concat(message.Seqno).ToArray());
    }
}
