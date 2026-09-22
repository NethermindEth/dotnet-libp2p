// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Dto;

namespace Nethermind.Libp2p.Protocols;

public class PubsubPeerDiscoveryProtocol(PubsubRouter pubSubRouter, PeerStore peerStore, PubsubPeerDiscoverySettings settings, ILocalPeer peer, ILoggerFactory? loggerFactory = null) : IDiscoveryProtocol, IDisposable, IAsyncDisposable
{
    private readonly PubsubRouter _pubSubRouter = pubSubRouter;
    private IReadOnlyList<Multiaddress>? _localPeerAddrs;
    private PeerId? localPeerId;
    private ITopic[]? topics;
    private readonly PubsubPeerDiscoverySettings _settings = settings;
    private readonly ILogger? logger = loggerFactory?.CreateLogger<PubsubPeerDiscoveryProtocol>();

    private readonly object _lifecycleLock = new();
    private bool _disposed;
    private CancellationTokenSource? _lifetime;
    private Task _run = Task.CompletedTask;

    /// <summary>
    /// Subscribes to the discovery topics and, unless <see cref="PubsubPeerDiscoverySettings.ListenOnly"/> is set, broadcasts the local peer.
    /// Discovery stops when <paramref name="token"/> is cancelled or when it is disposed; the router and peer store are not disposed.
    /// </summary>
    public Task StartDiscoveryAsync(IReadOnlyList<Multiaddress> localPeerAddrs, CancellationToken token = default)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lifetime is not null)
            {
                throw new InvalidOperationException("Discovery has been already started");
            }

            _localPeerAddrs = localPeerAddrs;
            localPeerId = localPeerAddrs.First().GetPeerId();

            topics = _settings.Topics.Select(topic =>
            {
                ITopic subscription = _pubSubRouter.GetTopic(topic);
                subscription.OnMessage += OnPeerMessage;
                return subscription;
            }).ToArray();

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _lifetime.Token.Register(ReleaseTopics);

            if (!_settings.ListenOnly)
            {
                _run = RunAsync(_lifetime.Token);
            }
        }

        return Task.CompletedTask;
    }

    private void ReleaseTopics()
    {
        try
        {
            foreach (ITopic topic in topics!)
            {
                topic.OnMessage -= OnPeerMessage;
                topic.Unsubscribe();
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Releasing discovery topics failed");
        }
    }

    /// <summary>
    /// Stops discovery without waiting for the broadcast loop to finish.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? lifetime;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            lifetime = _lifetime;
        }

        lifetime?.Cancel();
        lifetime?.Dispose();
    }

    /// <summary>
    /// Stops discovery like <see cref="Dispose"/> and waits for the broadcast loop to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();

        await _run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_run.IsFaulted)
        {
            logger?.LogWarning(_run.Exception, "Discovery broadcast failed");
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_settings.Interval, token);
            BroadcastPeerInfo();
        }
    }

    internal void BroadcastPeerInfo()
    {
        if (topics is null)
        {
            throw new NullReferenceException($"{nameof(topics)} should be previously set in ${nameof(StartDiscoveryAsync)}");
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

    private void OnPeerMessage(PeerId _, byte[] msg)
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
