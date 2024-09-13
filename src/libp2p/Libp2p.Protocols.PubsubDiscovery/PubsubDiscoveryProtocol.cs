// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Google.Protobuf;
using System.Diagnostics;

namespace Libp2p.Protocols.PubSubDiscovery;
public class PubSubDiscoverySettings
{
    public string[] Topics { get; set; } = ["_peer-discovery._p2p._pubsub"];
    public int Interval { get; set; } = 10_000;
    public bool ListenOnly { get; set; }
}

public class PubSubDiscoveryProtocol(PubsubRouter pubSubRouter, PubSubDiscoverySettings settings, PeerStore peerStore, ILocalPeer peer) : IDiscoveryProtocol
{
    private readonly PubsubRouter _pubSubRouter = pubSubRouter;
    private Multiaddress? _localPeerAddr;
    private readonly PubSubDiscoverySettings _settings = settings;

    public async Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
    {
        _localPeerAddr = localPeerAddr;
        ITopicSubscription[] topics = _settings.Topics.Select(topic =>
        {
            ITopicSubscription subscription = _pubSubRouter.Subscribe(topic);
            subscription.OnMessage += OnPeerMessage;
            return subscription;
        }).ToArray();

        token.Register(() =>
        {
            foreach (var topic in topics)
            {
                topic.Unsubscribe();
            }
        });

        if (!_settings.ListenOnly)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_settings.Interval, token);
                foreach (var topic in topics)
                {
                    topic.Publish(new Peer
                    {
                        PublicKey = peer.Identity.PublicKey.ToByteString(),
                        Addrs = { ByteString.CopyFrom(peer.Address.ToBytes()) },
                    });
                }
            }
        }
    }

    private void OnPeerMessage(byte[] msg)
    {
        try
        {
            Peer peer = Peer.Parser.ParseFrom(msg);
            Multiaddress[] addrs = [.. peer.Addrs.Select(a => Multiaddress.Decode(a.ToByteArray()))];
            PeerId? remotePeerId = addrs.FirstOrDefault()?.GetPeerId();
            if (remotePeerId is not null && remotePeerId != _localPeerAddr?.GetPeerId()!)
            {
                peerStore.Discover(addrs);
            }
            Debug.WriteLine($"{_localPeerAddr}: New peer discovered {peer}");
        }
        catch
        {

        }
    }
}

