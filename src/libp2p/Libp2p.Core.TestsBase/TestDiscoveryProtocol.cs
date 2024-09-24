// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Core.TestsBase;

//public class TestDiscoveryProtocol : IDiscoveryProtocol
//{
//    public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
//    public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

//    public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
//    {
//        TaskCompletionSource task = new();
//        token.Register(task.SetResult);
//        return task.Task;
//    }
//}
