// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Google.Protobuf;

namespace Libp2p.Protocols.PubSubDiscovery;
public class PubSubDiscoverySettings
{
    public string[] Topics { get; set; } = ["_peer-discovery._p2p._pubsub"];
    public int Interval { get; set; } = 10_000;
    public bool ListenOnly { get; set; }
}

public class PubSubDiscovery(PubsubRouter pubSubRouter, PubSubDiscoverySettings settings, ILocalPeer peer) : IDiscoveryProtocol
{
    private readonly PubsubRouter _pubSubRouter = pubSubRouter;
    private readonly PubSubDiscoverySettings _settings = settings;

    public async Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
    {
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
                        PublicKey = peer.Identity.PrivateKey.ToByteString(),
                        Addrs = { ByteString.CopyFrom(peer.Address.ToBytes()) },
                    }.ToByteArray());
                }
            }
        }
    }
    private void OnPeerMessage(byte[] msg)
    {
        Peer peer = Peer.Parser.ParseFrom(msg);
        OnAddPeer?.Invoke([.. peer.Addrs.Select(a => Multiaddress.Decode(a.ToByteArray()))]);
    }

    public Func<Multiaddress[], bool>? OnAddPeer { private get; set; }

    public Func<Multiaddress[], bool>? OnRemovePeer { private get; set; }

}

