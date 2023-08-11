// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;

namespace Libp2p.Protocols.Floodsub;
public partial class FloodsubRouter
{
    class PubSubPeer
    {
        public Action<Rpc> SendRpc { get; internal set; }
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId RawPeerId { get; init; }
    }
}
