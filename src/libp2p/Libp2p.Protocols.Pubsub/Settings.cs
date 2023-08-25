// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class Settings
{
    public const int D = 6; //The desired outbound degree of the network 	6
    public const int D_low = 4; //Lower bound for outbound degree 	4
    public const int D_high = 12;//Upper bound for outbound degree 	12
    public const int D_lazy = D;//(Optional) the outbound degree for gossip emission  D
    public const int heartbeat_interval = 1;//Time between heartbeats 	1 second
    public const int fanout_ttl = 60;//Time-to-live for each topic's fanout state 	60 seconds
    public const int mcache_len = 5;//Number of history windows in message cache 	5
    public const int mcache_gossip = 3;//Number of history windows to use when emitting gossip 	3
    public const int seen_ttl = 2 * 60;//Expiry time for cache of seen message ids 	2 minutes
}
