// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public class Settings
{
    public static Settings Default => new();

    public int Degree { get; set; } = 6; //The desired outbound degree of the network 	6
    public int LowestDegree { get; set; } = 4; //Lower bound for outbound degree 	4
    public int HighestDegree { get; set; } = 12;//Upper bound for outbound degree 	12
    public int LazyDegree { get; set; } = 6;//(Optional) the outbound degree for gossip emission D
    public int HeartbeatInterval { get; set; } = 1_000;//Time between heartbeats 	1 second
    public int FanoutTtl { get; set; } = 60 * 1000;//Time-to-live for each topic's fanout state 	60 seconds
    public int mcache_len { get; set; } = 5;//Number of history windows in message cache 	5
    public int mcache_gossip { get; set; } = 3;//Number of history windows to use when emitting gossip 	3
    public int MessageCacheTtl { get; set; } = 2 * 60 * 1000;//Expiry time for cache of seen message ids 	2 minutes
    public SignaturePolicy DefaultSignaturePolicy { get; set; } = SignaturePolicy.StrictSign;

    public Func<Message, string> GetMessageIdFunction = GetMessageId;

    private static string GetMessageId(Message message)
    {
        Span<byte> bytes = new byte[message.From.Length + message.Seqno.Length];
        return "";
    }

    public enum SignaturePolicy
    {
        StrictSign,
        StrictNoSign,
    }
}
