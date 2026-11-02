// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public interface IRoutingStateContainer
{
    /// <summary>Remote floodsub memberships. Empty topic keys are removed when the last peer leaves.</summary>
    ConcurrentDictionary<string, HashSet<PeerId>> FloodsubPeers { get; }
    /// <summary>Remote gossipsub memberships, independent of local subscriptions. Empty topic keys are removed.</summary>
    ConcurrentDictionary<string, HashSet<PeerId>> GossipsubPeers { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> Mesh { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> Fanout { get; }
    ConcurrentDictionary<string, DateTime> FanoutLastPublished { get; }
    ICollection<PeerId> ConnectedPeers { get; }
    bool Started { get; }
    Task Heartbeat();
}
