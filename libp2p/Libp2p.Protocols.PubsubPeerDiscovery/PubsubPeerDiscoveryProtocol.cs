// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Dto;
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
            BroadcastPeerInfo();
            await Task.Delay(_settings.Interval, token);
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
            Peer announcement = Peer.Parser.ParseFrom(msg);
            PeerId? announced = AnnouncedPeer(announcement, out Multiaddress[] addrs, logger);
            if (announced is null || announced == localPeerId!)
            {
                return;
            }

            peerStore.Discover(addrs, isTrusted: false);
            logger?.LogDebug("New peer discovered {peerId} at {addresses}", announced, string.Join(",", addrs));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Peer message handling caused an exception");
        }
    }

    /// <summary>
    /// The peer an announcement is internally consistent about, or <c>null</c> when it is not.
    /// <para>
    /// <b>This does not authenticate the announcer.</b> A public key is public, so anyone can put
    /// another peer's key in an announcement and have every check here pass; what this establishes
    /// is that one announcement describes one peer, not that the sender is that peer. Authenticating
    /// it needs the signed pubsub author, which subscribers are not currently given — see #242.
    /// Addresses from here therefore stay <c>isTrusted: false</c>.
    /// </para>
    /// <para>
    /// The identity is taken from the advertised public key, as in the reference implementation,
    /// rather than from an address suffix. Both are self-asserted, but reading only the first
    /// suffix meant the rest of the announcement was never checked against anything at all. Every
    /// suffix present must now agree with the derived id, because one announcement carrying two
    /// identities is not a thing an honest peer sends.
    /// </para>
    /// <para>
    /// An address that cannot be decoded is skipped rather than failing the announcement: a peer
    /// advertises every transport it listens on and this build does not know them all. An address
    /// claiming a different peer is a different matter and rejects the whole announcement.
    /// </para>
    /// </summary>
    internal static PeerId? AnnouncedPeer(Peer announcement, out Multiaddress[] addresses, ILogger? logger = null)
    {
        addresses = [];

        if (announcement.PublicKey.IsEmpty)
        {
            logger?.LogDebug("Ignoring a peer announcement that carries no public key");
            return null;
        }

        PeerId derived;
        try
        {
            derived = new PeerId(PublicKeyDecoder.Decode(announcement.PublicKey));
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ignoring a peer announcement whose public key is unusable");
            return null;
        }

        List<Multiaddress> decoded = [];
        foreach (ByteString raw in announcement.Addrs)
        {
            Multiaddress address;
            try
            {
                address = Multiaddress.Decode(raw.ToByteArray());
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Skipping an address of {peerId} that could not be decoded", derived);
                continue;
            }

            if (address.Protocols.Count == 0)
            {
                // Decoding is lenient: bytes it cannot place come back as an address with nothing
                // in it rather than as an exception. Undialable either way, so it goes the same way.
                logger?.LogDebug("Skipping an empty address of {peerId}", derived);
                continue;
            }

            PeerId? claimed = address.GetPeerId();
            if (claimed is not null && claimed != derived)
            {
                logger?.LogWarning("Rejecting an announcement for {peerId} that also claims {other}", derived, claimed);
                addresses = [];
                return null;
            }

            decoded.Add(address);
        }

        addresses = [.. decoded];
        return derived;
    }
}
