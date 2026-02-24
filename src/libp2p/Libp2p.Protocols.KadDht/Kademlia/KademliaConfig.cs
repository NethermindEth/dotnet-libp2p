// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.Kademlia;

public class KademliaConfig<TNode>
{
    /// <summary>
    /// The current node id
    /// </summary>
    public TNode CurrentNodeId { get; set; } = default!;

    /// <summary>
    /// K, as in the size of the kbucket.
    /// Default: 20 per libp2p Kademlia DHT spec.
    /// </summary>
    public int KSize { get; set; } = 20;

    /// <summary>
    /// Alpha, as in the parallelism of the lookup algorithm.
    /// Default: 10 per libp2p Kademlia DHT spec.
    /// </summary>
    public int Alpha { get; set; } = 10;

    /// <summary>
    /// Beta, as in B in kademlia the kademlia paper, 4.2 Accelerated Lookups
    /// </summary>
    public int Beta { get; set; } = 2;

    /// <summary>
    /// The interval on which a table refresh is initiated.
    /// Default: 1 hour per libp2p Kademlia DHT spec.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The timeout for each find neighbour call lookup
    /// </summary>
    public TimeSpan LookupFindNeighbourHardTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The timeout for a ping message during a refresh after which the node is considered to be offline.
    /// </summary>
    public TimeSpan RefreshPingTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many time a request for a node failed before we remove it from the routing table.
    /// </summary>
    public int NodeRequestFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Starting boot nodes.
    /// </summary>
    public IReadOnlyList<TNode> BootNodes { get; set; } = [];
}
