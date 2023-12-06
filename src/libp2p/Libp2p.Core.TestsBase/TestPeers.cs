// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Dto;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestPeers
{
    private static readonly ConcurrentDictionary<int, Multiaddress> testPeerAddrs = new();
    private static readonly ConcurrentDictionary<int, PeerId> testPeerIds = new();

    public static Multiaddress Multiaddr(int i) => testPeerAddrs.GetOrAdd(i, i =>
    {
        byte[] key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(32 - 4, 4), i);
        return Multiaddress.Decode($"/p2p/{new Identity(key).PeerId}");
    });

    public static PeerId PeerId(int i) => testPeerIds.GetOrAdd(i, i => new PeerId(testPeerAddrs[i].Get<P2P>().ToString()));

    public static PeerId PeerId(Multiaddress addr) => new PeerId(addr.Get<P2P>().ToString());

    public static Identity Identity(Multiaddress addr) => new(Core.PeerId.ExtractPublicKey(PeerId(addr).Bytes));

}
