// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestPeers
{
    private static readonly ConcurrentDictionary<int, Identity> testPeerIdentities = new();

    public static Identity Identity(int i) => testPeerIdentities.GetOrAdd(i, i =>
    {
        byte[] key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(32 - 4, 4), i);
        return new Identity(key);
    });

    public static PeerId PeerId(int i) => Identity(i).PeerId;
    public static Multiaddress Multiaddr(int i) => $"/p2p/{Identity(i).PeerId}";
    public static Identity Identity(Multiaddress addr) => testPeerIdentities.First(i => $"/p2p/{i}" == addr.ToString()).Value;


}
