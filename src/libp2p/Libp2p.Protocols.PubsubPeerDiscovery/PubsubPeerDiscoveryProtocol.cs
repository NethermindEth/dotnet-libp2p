// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Dto;

namespace Nethermind.Libp2p.Protocols;

public class PubsubPeerDiscoverySettings
{
    public string[] Topics { get; set; } = ["_peer-discovery._p2p._pubsub"];
    public int Interval { get; set; } = 10_000;
    public bool ListenOnly { get; set; }
}

public class PubsubPeerDiscoveryProtocol(PubsubRouter pubSubRouter, PeerStore peerStore, PubsubPeerDiscoverySettings settings, IPeer peer, ILoggerFactory? loggerFactory = null) : IDiscoveryProtocol
{
    private readonly PubsubRouter _pubSubRouter = pubSubRouter;
    private IReadOnlyList<Multiaddress>? _localPeerAddrs;
    private PeerId? localPeerId;
    private ITopic[]? topics;
    private readonly PubsubPeerDiscoverySettings _settings = settings;
    private ILogger? logger = loggerFactory?.CreateLogger<PubsubPeerDiscoveryProtocol>();

    public async Task DiscoverAsync(IReadOnlyList<Multiaddress> localPeerAddrs, CancellationToken token = default)
    {
        _localPeerAddrs = localPeerAddrs;
        localPeerId = localPeerAddrs.First().GetPeerId();


        topics = _settings.Topics.Select(topic =>
        {
            ITopic subscription = _pubSubRouter.GetTopic(topic);
            subscription.OnMessage += OnPeerMessage;
            return subscription;
        }).ToArray();

        token.Register(() =>
        {
            foreach (ITopic topic in topics)
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

        foreach (ITopic topic in topics)
        {
            topic.Publish(new Peer
            {
                PublicKey = peer.Identity.PublicKey.ToByteString(),
                Addrs = { peer.ListenAddresses.Select(a => ByteString.CopyFrom(a.ToBytes())) },
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
            if (remotePeerId is not null && remotePeerId != localPeerId!)
            {
                peerStore.Discover(addrs);
            }
            logger?.LogDebug($"New peer discovered {peer}");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Peer message handling caused an exception");
        }
    }
}

