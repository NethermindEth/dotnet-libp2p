// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

public class PubSubDiscoverySettings
{
    public string[] Topics { get; set; } = ["_peer-discovery._p2p._pubsub"];
    public int Interval { get; set; } = 10_000;
    public bool ListenOnly { get; set; }
}

public class PubSubDiscoveryProtocol(PubsubRouter pubSubRouter, PeerStore peerStore, PubSubDiscoverySettings settings, ILocalPeer peer, ILoggerFactory? loggerFactory = null) : IDiscoveryProtocol
{
    private readonly PubsubRouter _pubSubRouter = pubSubRouter;
    private Multiaddress? _localPeerAddr;
    private ITopic[]? topics;
    private readonly PubSubDiscoverySettings _settings = settings;
    private ILogger? logger = loggerFactory?.CreateLogger<PubSubDiscoveryProtocol>();

    public async Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
    {
        _localPeerAddr = localPeerAddr;
        topics = _settings.Topics.Select(topic =>
        {
            ITopic subscription = _pubSubRouter.GetTopic(topic);
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
                BroadcastPeerInfo();
            }
        }
    }

    internal void BroadcastPeerInfo()
    {
        if (topics is null)
        {
            throw new NullReferenceException($"{nameof(topics)} should be previously set in ${nameof(DiscoverAsync)}");
        }

        foreach (var topic in topics)
        {
            topic.Publish(new Peer
            {
                PublicKey = peer.Identity.PublicKey.ToByteString(),
                Addrs = { ByteString.CopyFrom(peer.Address.ToBytes()) },
            });
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
            logger?.LogDebug($"{_localPeerAddr}: New peer discovered {peer}");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Peer message handling caused an exception");
        }
    }
}

