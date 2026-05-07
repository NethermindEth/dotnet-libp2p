// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Integration;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Transport;

/// <summary>
/// Minimal address book for TestNode so we can exercise real dialing.
/// In production, replace with your domain node type or back with PeerStore.
/// </summary>
public sealed class TestNodeAddressBook
{
    private readonly ConcurrentDictionary<PeerId, Multiaddress[]> _map = new();

    public void SetAddresses(TestNode node, params Multiaddress[] addrs) => _map[node.Id] = addrs;

    public Multiaddress[]? TryGet(TestNode node) => _map.TryGetValue(node.Id, out var addrs) ? addrs : null;
}
