// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Org.BouncyCastle.Crypto.Paddings;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

class TestDiscoveryProtocol : IDiscoveryProtocol
{
    public Func<Multiaddr[], bool>? OnAddPeer { get; set; }
    public Func<Multiaddr[], bool>? OnRemovePeer { get; set; }

    public Task DiscoverAsync(Multiaddr localPeerAddr, CancellationToken token = default)
    {
        var task = new TaskCompletionSource();
        token.Register(task.SetResult);
        return task.Task;
    }
}

class TestLocalPeer : ILocalPeer
{
    public Identity Identity { get; set; }
    public Multiaddr Address { get; set; }

    public Task<IRemotePeer> DialAsync(Multiaddr addr, CancellationToken token = default)
    {
        return Task.FromResult<IRemotePeer>(null);
    }

    public Task<IListener> ListenAsync(Multiaddr addr, CancellationToken token = default)
    {
        return Task.FromResult<IListener>(null);
    }
}

[TestFixture]
public class FloodsubProtocolTests
{
    private static ConcurrentDictionary<int, string> testPeers = new();
    private static string MakePeer(int i) => testPeers.GetOrAdd(i, i =>
        {
            var key = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(32 - 4, 4), i);
            return new Identity(key).PeerId;
        });


    [Test]
    public async Task Test()
    {
        var router = new PubsubRouter();
        var state = router as IRoutingStateContainer;

        ILocalPeer peer = new TestLocalPeer();
        TestDiscoveryProtocol discovery = new TestDiscoveryProtocol();
        CancellationToken token = default;
        _ = router.RunAsync(peer, discovery, token: token);
        Assert.IsNotNull(discovery.OnAddPeer);
        discovery.OnAddPeer(new Multiaddr[] { $"/p2p/{MakePeer(1)}" });
        state.FloodsubPeers.Keys.Contains(MakePeer(1));
    }
}
