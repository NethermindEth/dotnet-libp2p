// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;

public class PubsubPeerDiscoverySettings
{
    public string[] Topics { get; set; } = ["_peer-discovery._p2p._pubsub"];
    public int Interval { get; set; } = 10_000;
    public bool ListenOnly { get; set; }
}

