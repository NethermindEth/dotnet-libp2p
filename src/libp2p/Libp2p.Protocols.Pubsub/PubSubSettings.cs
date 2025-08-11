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


    public static MessageId ConcatFromAndSeqno(Message message) => new([.. message.From, .. message.Seqno]);
}
