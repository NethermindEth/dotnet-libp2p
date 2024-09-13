// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.PubSubDiscovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NUnit.Framework.Internal;
using System.Data.Common;
using System.Diagnostics.Metrics;

namespace Libp2p.Protocols.Multistream.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class MultistreamProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake2()
    {
        IPeerFactory peerFactory = new TestBuilder().Build();
        ChannelBus commonBus = new();

        ServiceProvider sp1 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        ServiceProvider sp2 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        ILocalPeer peerA = sp1.GetService<IPeerFactory>()!.Create(TestPeers.Identity(1));
        await peerA.ListenAsync(TestPeers.Multiaddr(1));
        ILocalPeer peerB = sp2.GetService<IPeerFactory>()!.Create(TestPeers.Identity(2));
        await peerB.ListenAsync(TestPeers.Multiaddr(2));

        IRemotePeer remotePeerB = await peerA.DialAsync(peerB.Address);
        await remotePeerB.DialAsync<GossipsubProtocol>();
    }

    [Test]
    public async Task Test_Bus()
    {
        int totalCount = 3;
        TestContextLoggerFactory fac = new TestContextLoggerFactory();
        // There is common communication point
        ChannelBus bus = new(fac);
        //bus.GetIncomingRequests();
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        int totalCount = 20;
        TestContextLoggerFactory fac = new TestContextLoggerFactory();
        // There is common communication point
        ChannelBus commonBus = new(fac);
        ILocalPeer[] peers = new ILocalPeer[totalCount];
        PeerStore[] peerStores = new PeerStore[totalCount];
        PubsubRouter[] routers = new PubsubRouter[totalCount];


        for (int i = 0; i < totalCount; i++)
        {
            // But we create a seprate setup for every peer
            ServiceProvider sp = new ServiceCollection()
                   .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
                   .AddSingleton<ILoggerFactory>(sp => fac)
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton<PeerStore>()
                   .AddSingleton(sp=> sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
            ILocalPeer peer = peers[i] = peerFactory.Create(TestPeers.Identity(i));
            PubsubRouter router = routers[i] = sp.GetService<PubsubRouter>()!;
            PeerStore peerStore = sp.GetService<PeerStore>()!;
            PubSubDiscoveryProtocol disc = new(router, new PubSubDiscoverySettings() { Interval = 300 }, peerStore, peer);
            _ = router.RunAsync(peer, peerStore);
            peerStores[i] = peerStore;
            _ = disc.DiscoverAsync(peers[i].Address);
        }

        await Task.Delay(1000);

        for (int i = 0; i < peers.Length; i++)
        {
            peerStores[i].Discover([peers[(i + 1) % totalCount].Address]);
        }

        await Task.Delay(50000);
    }

    //[Test]
    //public async Task Derp()
    //{


    //    internal CancellationToken OutboundConnection(Multiaddress addr, string protocolId, Task dialTask, Action<Rpc> sendRpc)
    //    {
    //        logger?.LogDebug($"{localPeer?.Identity.PeerId}-out");
    //        PeerId? peerId = addr.GetPeerId();

    //        if (peerId is null)
    //        {
    //            logger?.LogDebug($"{localPeer?.Identity.PeerId}-out: 1 peerid null");
    //            return Canceled;
    //        }

    //        PubsubPeer peer = peerState.GetOrAdd(peerId, (id) => new PubsubPeer(peerId, protocolId) { Address = addr, SendRpc = sendRpc, InititatedBy = ConnectionInitiation.Local });

    //        lock (peer)
    //        {
    //            if (peer.SendRpc == sendRpc)
    //            {
    //                logger?.LogDebug($"{localPeer?.Identity.PeerId}-out: 2 SendRpc null");
    //            }
    //            else
    //            {
    //                if (peer.SendRpc is null)
    //                {
    //                    peer.SendRpc = sendRpc;
    //                }
    //                else
    //                {
    //                    logger?.LogDebug($"{localPeer?.Identity.PeerId}-out: 3 SendRpc not null, cancelled");
    //                    return Canceled;
    //                }
    //            }
    //        }

    //        dialTask.ContinueWith(t =>
    //        {
    //            peerState.GetValueOrDefault(peerId)?.TokenSource.Cancel();
    //            peerState.TryRemove(peerId, out _);
    //            foreach (var topicPeers in fPeers)
    //            {
    //                topicPeers.Value.Remove(peerId);
    //            }
    //            foreach (var topicPeers in gPeers)
    //            {
    //                topicPeers.Value.Remove(peerId);
    //            }
    //            foreach (var topicPeers in fanout)
    //            {
    //                topicPeers.Value.Remove(peerId);
    //            }
    //            foreach (var topicPeers in mesh)
    //            {
    //                topicPeers.Value.Remove(peerId);
    //            }
    //            reconnections.Add(new Reconnection([addr], settings.ReconnectionAttempts));
    //        });
    //        logger?.LogDebug($"{localPeer?.Identity.PeerId}-out: 4 send hello {string.Join(",", topicState.Keys)}");

    //        Rpc helloMessage = new Rpc().WithTopics(topicState.Keys.ToList(), Enumerable.Empty<string>());
    //        peer.Send(helloMessage);
    //        logger?.LogDebug("Outbound {peerId}", peerId);

    //        logger?.LogDebug($"{localPeer?.Identity.PeerId}-out: 5 return token {peer.TokenSource.Token}");

    //        return peer.TokenSource.Token;
    //    }

    //    internal CancellationToken InboundConnection(Multiaddress addr, string protocolId, Task listTask, Task dialTask, Func<Task> subDial)
    //    {
    //        PeerId? peerId = addr.GetPeerId();
    //        logger?.LogDebug($"{localPeer?.Identity.PeerId}-in: 1 remote peer {peerId}");

    //        if (peerId is null || peerId == localPeer!.Identity.PeerId)
    //        {
    //            logger?.LogDebug($"{localPeer?.Identity.PeerId}-in: 1.1 remote cancel {peerId}");

    //            return Canceled;
    //        }

    //        logger?.LogDebug("Inbound {peerId}", peerId);

    //        PubsubPeer? newPeer = null;
    //        PubsubPeer existingPeer = peerState.GetOrAdd(peerId, (id) => newPeer = new PubsubPeer(peerId, protocolId) { Address = addr, InititatedBy = ConnectionInitiation.Remote });
    //        if (newPeer is not null)
    //        {
    //            logger?.LogDebug($"{localPeer?.Identity.PeerId}-in: 2 new peer {peerId}");
    //            //logger?.LogDebug("Inbound, let's dial {peerId} via remotely initiated connection", peerId);
    //            listTask.ContinueWith(t =>
    //            {
    //                peerState.GetValueOrDefault(peerId)?.TokenSource.Cancel();
    //                peerState.TryRemove(peerId, out _);
    //                foreach (var topicPeers in fPeers)
    //                {
    //                    topicPeers.Value.Remove(peerId);
    //                }
    //                foreach (var topicPeers in gPeers)
    //                {
    //                    topicPeers.Value.Remove(peerId);
    //                }
    //                foreach (var topicPeers in fanout)
    //                {
    //                    topicPeers.Value.Remove(peerId);
    //                }
    //                foreach (var topicPeers in mesh)
    //                {
    //                    topicPeers.Value.Remove(peerId);
    //                }
    //                reconnections.Add(new Reconnection([addr], settings.ReconnectionAttempts));
    //            });

    //            subDial();
    //            logger?.LogDebug($"{localPeer?.Identity.PeerId}-in: 3 subdialed");

    //            return newPeer.TokenSource.Token;
    //        }
    //        else
    //        {
    //            logger?.LogDebug($"{localPeer?.Identity.PeerId}-in: 1.2 new peer null");
    //            return existingPeer.TokenSource.Token;
    //        }
    //    }
    //}
}
