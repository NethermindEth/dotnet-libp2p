// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestPeers
{
    private static readonly ConcurrentDictionary<int, Multiaddr> testPeerAddrs = new();
    private static readonly ConcurrentDictionary<int, PeerId> testPeerIds = new();

    public static Multiaddr Multiaddr(int i) => testPeerAddrs.GetOrAdd(i, i =>
    {
        byte[] key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(32 - 4, 4), i);
        return new Multiaddr($"/p2p/{new Identity(key).PeerId}");
    });

    public static PeerId PeerId(int i) => testPeerIds.GetOrAdd(i, i => new PeerId(testPeerAddrs[i].At(Enums.Multiaddr.P2p)!));

    public static PeerId PeerId(Multiaddr addr) => new(addr.At(Enums.Multiaddr.P2p)!);

    public static Identity Identity(Multiaddr addr) => new(Core.PeerId.ExtractPublicKey(PeerId(addr).Bytes));

}
