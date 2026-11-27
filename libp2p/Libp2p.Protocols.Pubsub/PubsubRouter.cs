// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter : IRoutingStateContainer, IDisposable, IAsyncDisposable
{
    static int routerCounter = 0;
    readonly int routerId = Interlocked.Increment(ref routerCounter);

    public override string ToString()
    {
        return $"Router#{routerId}: {localPeer?.Identity.PeerId ?? "null"}, " +
            $"peers: {peerState.Count(x => x.Value.HasWriter)}/{peerState.Count} ({string.Join(",", peerState.Keys)}), " +
            $"mesh: {string.Join("|", mesh.Select(m => $"{m.Key}:{m.Value.Count}"))}, " +
            $"fanout: {string.Join("|", fanout.Select(m => $"{m.Key}:{m.Value.Count}"))}, " +
            $"fPeers: {string.Join("|", fPeers.Select(m => $"{m.Key}:{m.Value.Count}"))}, " +
            $"gPeers: {string.Join("|", gPeers.Select(m => $"{m.Key}:{m.Value.Count}"))}";
    }

    public const string FloodsubProtocolVersion = "/floodsub/1.0.0";
    public const string GossipsubProtocolVersionV10 = "/meshsub/1.0.0";
    public const string GossipsubProtocolVersionV11 = "/meshsub/1.1.0";
    public const string GossipsubProtocolVersionV12 = "/meshsub/1.2.0";
    public const string GossipsubProtocolVersionV13 = "/meshsub/1.3.0";

    internal sealed partial class PubsubPeer
    {
        // Membership and byte accounting are owned by the router lock, independently of local interest.
        public HashSet<string> RemoteSubscriptions { get; } = new(StringComparer.Ordinal);
        public long RemoteSubscriptionTopicBytes { get; set; }

        public PubsubPeer(PubsubRouter router, PeerId peerId, string protocolId, PubsubSettings settings, ReconnectionState reconnection, Func<Task> disconnect)
        {
            PeerId = peerId;
            Reconnection = reconnection;
            Protocol = protocolId switch
            {
                GossipsubProtocolVersionV10 => PubsubProtocol.GossipsubV10,
                GossipsubProtocolVersionV11 => PubsubProtocol.GossipsubV11,
                GossipsubProtocolVersionV12 => PubsubProtocol.GossipsubV12,
                GossipsubProtocolVersionV13 => PubsubProtocol.GossipsubV13,
                _ => PubsubProtocol.Floodsub,
            };
            _advertisesPartialMessages = settings.EnablePartialMessages;
            TokenSource = new CancellationTokenSource();
            Backoff = [];
            _router = router;
            Disconnect = disconnect;
            Score = new PeerScore(settings);
            Control = new PeerControlState();
        }

        public enum PubsubProtocol
        {
            None = 0,
            Floodsub = 1,
            GossipsubV10 = 2,
            GossipsubV11 = 4,
            GossipsubV12 = 8,
            GossipsubV13 = 16,
            AnyGossipsub = GossipsubV10 | GossipsubV11 | GossipsubV12 | GossipsubV13,
        }

        internal Rpc RemoveUnsupportedPartialSubscriptionOptions(Rpc rpc)
        {
            if (SupportsExtensions || !rpc.Subscriptions.Any(subscription => subscription.HasRequestsPartial || subscription.HasSupportsSendingPartial))
            {
                return rpc;
            }

            Rpc filteredRpc = rpc.Clone();
            foreach (Rpc.Types.SubOpts subscription in filteredRpc.Subscriptions)
            {
                subscription.ClearRequestsPartial();
                subscription.ClearSupportsSendingPartial();
            }

            return filteredRpc;
        }

        public Dictionary<string, DateTime> Backoff { get; internal set; }
        private readonly bool _advertisesPartialMessages;
        private readonly ConcurrentDictionary<string, PartialMessagesSubscription> _partialMessagesSubscriptions = new();

        private readonly record struct PartialMessagesSubscription(bool RequestsPartialMessages, bool SupportsSendingPartialMessages);

        public bool ExtensionsSent { get; internal set; }
        public bool SupportsPartialMessagesExtension { get; set; }

        public bool NeedsExtensions
        {
            get
            {
                lock (_router._outboundLock)
                {
                    return _advertisesPartialMessages && SupportsExtensions && !ExtensionsQueued;
                }
            }
        }

        public void UpdatePartialMessagesSubscription(string topicId, bool requestsPartialMessages, bool supportsSendingPartialMessages)
        {
            // The extension registry makes sending support implicit for requesters.
            _partialMessagesSubscriptions[topicId] = new(requestsPartialMessages, requestsPartialMessages || supportsSendingPartialMessages);
        }

        public void RemovePartialMessagesSubscription(string topicId)
        {
            _partialMessagesSubscriptions.TryRemove(topicId, out _);
        }

        public bool RequestsPartialMessages(string topicId)
        {
            return _partialMessagesSubscriptions.TryGetValue(topicId, out PartialMessagesSubscription subscription) && subscription.RequestsPartialMessages;
        }

        public bool SupportsSendingPartialMessages(string topicId)
        {
            return _partialMessagesSubscriptions.TryGetValue(topicId, out PartialMessagesSubscription subscription) && subscription.SupportsSendingPartialMessages;
        }

        internal Rpc AddExtensionsIfNeeded(Rpc rpc)
        {
            if (!_advertisesPartialMessages || !SupportsExtensions || ExtensionsQueued)
            {
                return rpc;
            }

            Rpc extendedRpc = rpc.Clone();
            extendedRpc.Control ??= new ControlMessage();
            extendedRpc.Control.Extensions ??= new ControlExtensions();
            extendedRpc.Control.Extensions.PartialMessages = true;
            return extendedRpc;
        }

        public CancellationTokenSource TokenSource { get; init; }
        public PeerId PeerId { get; set; }

        public PubsubProtocol Protocol { get; set; }
        public bool IsGossipSub => (Protocol & PubsubProtocol.AnyGossipsub) != PubsubProtocol.None;
        public bool IsFloodSub => Protocol == PubsubProtocol.Floodsub;
        public bool SupportsPeerExchange => Protocol is PubsubProtocol.GossipsubV11 or PubsubProtocol.GossipsubV12 or PubsubProtocol.GossipsubV13;
        public bool SupportsExtensions => Protocol == PubsubProtocol.GossipsubV13;

        public ConnectionDirection Direction { get; internal set; }
        public Multiaddress Address { get; internal set; } = null!;
        public ReconnectionState Reconnection { get; }

        // Peer scoring (Gossipsub v1.1)
        public PeerScore Score { get; internal set; }
        public PeerControlState Control { get; }
    }

    private static readonly CancellationToken Canceled;

    #region IRoutingStateContainer
    ConcurrentDictionary<string, HashSet<PeerId>> IRoutingStateContainer.FloodsubPeers => fPeers;
    ConcurrentDictionary<string, HashSet<PeerId>> IRoutingStateContainer.GossipsubPeers => gPeers;
    ConcurrentDictionary<string, HashSet<PeerId>> IRoutingStateContainer.Mesh => mesh;
    ConcurrentDictionary<string, HashSet<PeerId>> IRoutingStateContainer.Fanout => fanout;
    ConcurrentDictionary<string, DateTime> IRoutingStateContainer.FanoutLastPublished => fanoutLastPublished;
    bool IRoutingStateContainer.Started => localPeer is not null;
    ICollection<PeerId> IRoutingStateContainer.ConnectedPeers => peerState.Keys;
    Task IRoutingStateContainer.Heartbeat() => Heartbeat();
    #endregion

    public event Action<string, PeerId, byte[]>? OnMessage;
    /// <summary>
    /// Raised for Gossipsub v1.3 Partial Messages extension payloads. The router
    /// does not retain their application-defined state.
    /// </summary>
    public event Action<string, PeerId, PartialMessage>? OnPartialMessage;

    /// <summary>
    /// Raised with a locally published partial-message group and non-mesh peers
    /// that requested it. Applications can respond by calling <see cref="SendPartial"/>
    /// for the supplied group. Cached full messages retain ordinary IHAVE gossip.
    /// </summary>
    public event Action<string, byte[], IReadOnlyList<PeerId>>? OnPartialGossip;

    /// <summary>
    /// Called outside the router lock only after a deferred message fits in the pending store.
    /// Start asynchronous validation here, then call <see cref="CompleteValidation"/> with this
    /// exact message instance before <see cref="PubsubSettings.PendingValidationTimeout"/> elapses.
    /// Return the validation task so the router can observe failures without waiting for it.
    /// The task must include the call to <see cref="CompleteValidation"/>; an entry still pending
    /// when the task finishes is discarded. Assign one owner for each verdict.
    /// Application-owned validation work is not cancelled or awaited when the router is disposed;
    /// a completion after disposal is ignored.
    /// </summary>
    private Func<PeerId, Message, Task>? _onDeferredMessage;
    public Func<PeerId, Message, Task>? OnDeferredMessage
    {
        get => _onDeferredMessage;
        set
        {
            if (value?.GetInvocationList().Length > 1)
            {
                throw new ArgumentException("Deferred validation has one owner.", nameof(value));
            }

            _onDeferredMessage = value;
        }
    }
    /// <summary>
    /// Perform only synchronous checks here. Return <see cref="MessageValidity.Deferred"/> to
    /// request admission to the bounded pending store; start asynchronous work in
    /// <see cref="OnDeferredMessage"/>. Do not mutate the message.
    /// </summary>
    public Func<PeerId, Message, MessageValidity>? VerifyMessage = null;

    internal int MaxRpcBytes => _settings.MaxRpcBytes;
    /// <summary>Number of unexpired messages awaiting an application validation verdict.</summary>
    public int PendingValidationCount
    {
        get
        {
            lock (this)
            {
                RemoveExpiredPendingValidations();
                return _pendingValidations.Count;
            }
        }
    }

    private readonly PubsubSettings _settings;
    private readonly MessageCache _messageCache;
    // Never disposed: an RPC in flight may still be queued on it or about to release it, and
    // disposing would strand the queued wait and fault the release. Nothing here takes its
    // AvailableWaitHandle, which is the only thing its disposal would free.
    private readonly SemaphoreSlim _messageIdCalculations;
    private readonly TtlCache<MessageId> _seenMessages;
    private readonly TtlCache<MessageId> _limboMessageCache;
    private readonly PartialMessageGossipCache partialMessageGossip;
    private readonly IwantPromiseTracker _iwantPromises;
    private readonly Dictionary<MessageId, PendingValidation> _pendingValidations = new(MessageIdComparer.Instance);
    private readonly TimeProvider _timeProvider;
    // Used only under the router lock; injected Random instances need not be thread-safe.
    private readonly Random _random;
    private int _pendingValidationBytes;

    private sealed record PendingValidation(MessageId Id, Message Original, Message Snapshot, PeerId Source, int Size)
    {
        public required DateTimeOffset ExpiresAt { get; set; }
    }

    private ILocalPeer? localPeer;
    private readonly ILogger? logger;

    // all floodsub peers in topics
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> fPeers = new();

    // all gossipsub peers in topics
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> gPeers = new();

    // gossip peers in mesh, which is subnet for message exchange
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> mesh = new();

    // gossip peers in mesh, which is subnet for message exchange for topics that we did not subscribe to, but we sent messages recently
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> fanout = new();
    private readonly ConcurrentDictionary<string, DateTime> fanoutLastPublished = new();

    // all peers with their connection status
    private readonly ConcurrentDictionary<PeerId, PubsubPeer> peerState = new();
    // Streams and retries keep their state alive; idle peer IDs need not be retained forever.
    private readonly ConcurrentDictionary<PeerId, WeakReference<ReconnectionState>> reconnectionStates = new();
    private DateTime nextReconnectionStateCleanup;

    private readonly IPeerStore _peerStore;
    private readonly IReadOnlyDictionary<PeerId, Multiaddress[]> directPeers;
    private readonly Dictionary<PeerId, OutboundDialAttempt> outboundDials = [];
    private long heartbeatTick;
    private ulong seqNo = 1;

    // Lifetime of work owned by the router: background loops and dials it starts itself.
    private readonly object _lifecycleLock = new();
    private bool _disposed;
    private CancellationTokenSource? _lifetime;
    private readonly CancellationTokenSource _stopped = new();
    private CancellationToken _lifetimeToken;
    private Action<Multiaddress[]>? _onNewPeer;
    private Task _loops = Task.CompletedTask;
    private readonly ConcurrentDictionary<Task, byte> _connects = new();

    private sealed class OutboundDialAttempt
    {
        public PubsubPeer? Owner { get; set; }
        public DeferredReply? Reply { get; set; }
        public ConnectionAttempt? Connection { get; set; }
    }
    private sealed record DeferredReply(PubsubPeer Peer, Func<Task> Dial);

    private ReconnectionState GetReconnectionState(PeerId peerId)
    {
        while (true)
        {
            WeakReference<ReconnectionState> reference = reconnectionStates.GetOrAdd(peerId, static id => new(new ReconnectionState(id)));
            if (reference.TryGetTarget(out ReconnectionState? state)) return state;

            state = new ReconnectionState(peerId);
            if (reconnectionStates.TryUpdate(peerId, new WeakReference<ReconnectionState>(state), reference)) return state;
        }
    }

    private void CleanupReconnectionStates()
    {
        if (DateTime.UtcNow < nextReconnectionStateCleanup) return;
        nextReconnectionStateCleanup = DateTime.UtcNow.AddMinutes(1);

        foreach (KeyValuePair<PeerId, WeakReference<ReconnectionState>> entry in reconnectionStates)
        {
            if (!entry.Value.TryGetTarget(out _))
            {
                ((ICollection<KeyValuePair<PeerId, WeakReference<ReconnectionState>>>)reconnectionStates).Remove(entry);
            }
        }
    }

    static PubsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public PubsubRouter(IPeerStore store, PubsubSettings? settings = null, ILoggerFactory? loggerFactory = default,
        TimeProvider? timeProvider = null, IPubsubReconnectPolicy? reconnectPolicy = null)
        : this(store, settings, loggerFactory, timeProvider, Random.Shared, reconnectPolicy)
    {
    }

    internal PubsubRouter(IPeerStore store, PubsubSettings? settings, ILoggerFactory? loggerFactory, TimeProvider? timeProvider,
        Random random, IPubsubReconnectPolicy? reconnectPolicy = null)
    {
        logger = loggerFactory?.CreateLogger("pubsub-router");

        _peerStore = store;
        _settings = settings ?? PubsubSettings.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reconnectPolicy = reconnectPolicy;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.OpportunisticGraftTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(_settings.OpportunisticGraftPeers);
        _random = random;
        _outboundLimits = new(_settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxIncomingMessageIdRpcEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxIncomingMessageIdRpcBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxIncomingMessageIdRpcEntriesPerPeer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxIncomingMessageIdRpcBytesPerPeer);
        if (_settings.MaxIncomingMessageIdRpcBytes < _settings.MaxRpcBytes)
        {
            // Otherwise a maximum-size RPC could never be admitted, even by an idle router.
            throw new ArgumentOutOfRangeException(nameof(settings), _settings.MaxIncomingMessageIdRpcBytes,
                $"{nameof(PubsubSettings.MaxIncomingMessageIdRpcBytes)} must be at least {nameof(PubsubSettings.MaxRpcBytes)}.");
        }
        _maxIncomingEntries = _settings.MaxIncomingMessageIdRpcEntries;
        _maxIncomingBytes = _settings.MaxIncomingMessageIdRpcBytes;
        _maxIncomingEntriesPerPeer = _settings.MaxIncomingMessageIdRpcEntriesPerPeer;
        _maxIncomingBytesPerPeer = _settings.MaxIncomingMessageIdRpcBytesPerPeer;
        ArgumentOutOfRangeException.ThrowIfNegative(_settings.ReconnectionAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(_settings.ReconnectionPeriod, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxPendingValidationMessages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxPendingValidationBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxRemoteSubscriptionsPerPeer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxRemoteSubscriptionTopicBytesPerPeer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxRemoteSubscriptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.MaxRemoteSubscriptionTopicBytes);
        if (_settings.PendingValidationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PubsubSettings.PendingValidationTimeout));
        }
        if (_settings.DefaultSignaturePolicy is PubsubSettings.SignaturePolicy.StrictNoSign && _settings.GetMessageId == PubsubSettings.ConcatFromAndSeqno)
        {
            throw new InvalidOperationException("StrictNoSign requires a custom GetMessageId function.");
        }

        if (_settings.DirectConnectPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PubsubSettings.DirectConnectPeriod), "DirectConnectPeriod must be positive.");
        }

        directPeers = CreateDirectPeers(_settings.DirectPeers);
        ValidateControlSettings(_settings);
        _messageCache = new(_settings.mcache_gossip, _settings.mcache_len, _settings.MaxMessageCacheEntries, _settings.MaxMessageCacheBytes);
        _messageIdCalculations = new(Math.Max(1, _settings.MaxConcurrentMessageIdCalculations));
        _seenMessages = new(_settings.MessageCacheTtl, _settings.MaxSeenMessageIds, _timeProvider, MessageIdComparer.Instance);
        _limboMessageCache = new(_settings.MessageCacheTtl, _settings.MaxSeenMessageIds, _timeProvider, MessageIdComparer.Instance);
        _iwantPromises = new(_settings.MaxIwantPromises);
        partialMessageGossip = new(_settings.MaxPartialMessageGroupsPerTopic, _settings.PartialMessageGossipTtlHeartbeats);
    }

    /// <summary>
    /// Starts background loops and, when <see cref="PubsubSettings.AutoConnect"/> is enabled,
    /// connects to peers from the <see cref="IPeerStore"/>.
    /// The router stops when <paramref name="token"/> is cancelled or when it is disposed;
    /// use <see cref="DisposeAsync"/> to also wait for the owned work to finish.
    /// </summary>
    public Task StartAsync(ILocalPeer localPeer, CancellationToken token = default)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            logger?.LogDebug($"Running pubsub for {string.Join(",", localPeer.ListenAddresses)}");

            if (this.localPeer is not null)
            {
                throw new InvalidOperationException("Router has been already started");
            }

            this.localPeer = localPeer;

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stopped.Token);
            CancellationToken lifetime = _lifetimeToken = _lifetime.Token;

            List<Task> loops = [
                Task.Run(() => LoopHeartbeat(lifetime), lifetime),
                Task.Run(() => LoopExpireMessageIds(lifetime), lifetime),
            ];
            if (_settings.AutoConnect)
            {
                loops.Add(Task.Run(() => LoopReconnect(lifetime), lifetime));
                if (directPeers.Count > 0)
                {
                    loops.Add(Task.Run(() => LoopReconnectDirectPeers(lifetime), lifetime));
                }
            }
            _loops = Task.WhenAll(loops);

            foreach (Multiaddress[] directPeerAddresses in directPeers.Values)
            {
                _peerStore.Discover(directPeerAddresses);
            }
            if (_settings.AutoConnect)
            {
                ConnectDirectPeers(lifetime);

                _onNewPeer = addrs => ConnectDiscoveredPeer(addrs, lifetime);
                // Subscribing replays already known peers, so it goes last.
                _peerStore.OnNewPeer += _onNewPeer;
            }
        }

        logger?.LogInformation("Started");
        return Task.CompletedTask;
    }

    private async Task LoopHeartbeat(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_settings.HeartbeatInterval, token);
            await Heartbeat();
        }
    }

    private async Task LoopExpireMessageIds(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, token).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _seenMessages.RemoveExpired(now);
            _limboMessageCache.RemoveExpired(now);
        }
    }

    private async Task LoopReconnectDirectPeers(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_settings.DirectConnectPeriod, token);
            ConnectDirectPeers(token);
        }
    }

    private void Track(Task connect)
    {
        _connects.TryAdd(connect, 0);
        _ = connect.ContinueWith(t =>
        {
            _connects.TryRemove(t, out _);
            if (t.IsFaulted) logger?.LogWarning(t.Exception, "Pubsub background work failed");
        }, TaskScheduler.Default);
    }

    private OutboundDialAttempt? ReserveOutbound(PeerId peerId)
    {
        lock (this)
        {
            if (IsStopping || outboundDials.ContainsKey(peerId) ||
                peerState.TryGetValue(peerId, out PubsubPeer? peer) && (peer.HasWriter || peer.Terminal)) return null;
            OutboundDialAttempt attempt = new();
            outboundDials.Add(peerId, attempt);
            return attempt;
        }
    }

    private void ReleaseOutbound(PeerId peerId, OutboundDialAttempt attempt, bool failed = false)
    {
        DeferredReply? reply;
        OutboundDialAttempt? outbound;
        TaskCompletionSource completion;
        lock (this)
        {
            if (!outboundDials.TryGetValue(peerId, out OutboundDialAttempt? current) || !ReferenceEquals(current, attempt))
                return;
            outboundDials.Remove(peerId);
            reply = attempt.Reply;
            attempt.Reply = null;
            if (reply is null || reply.Peer.Reconnection.Suppressed ||
                !peerState.TryGetValue(peerId, out PubsubPeer? peer) || !ReferenceEquals(peer, reply.Peer) ||
                (outbound = ReserveOutbound(peerId)) is null)
            {
                if (failed && attempt.Owner is { } owner)
                    QueueReconnection(owner.Reconnection, PubsubReconnectReason.ReplyFailed);
                SignalReconnect();
                return;
            }
            outbound.Owner = reply.Peer;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Track(completion.Task);
        }
        _ = OpenReplyAsync(reply.Peer, outbound, reply.Dial, completion);
    }

    /// <summary>
    /// Stops owned work and releases owned resources without waiting for background work to finish.
    /// Shared dependencies such as <see cref="IPeerStore"/> and the local peer are not disposed.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? lifetime;
        Action<Multiaddress[]>? onNewPeer;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            lifetime = _lifetime;
            onNewPeer = _onNewPeer;
        }

        try
        {
            if (onNewPeer is not null)
                _peerStore.OnNewPeer -= onNewPeer;
        }
        finally
        {
            try { _stopped.Cancel(); }
            finally
            {
                lifetime?.Dispose();

                lock (this)
                {
                    ClearReconnections();
                    reconnectionStates.Clear();
                    // Registration also holds this lock: no generation can escape the shutdown snapshot.
                    foreach (PubsubPeer peer in peerState.Values) StopPeer(peer);
                    _pendingValidations.Clear();
                    _pendingValidationBytes = 0;
                    foreach (PubsubPeer peer in peerState.Values)
                    {
                        RemoveRemoteSubscriptions(peer);
                    }
                }

                _messageCache.Clear();
                _seenMessages.Clear();
                _limboMessageCache.Clear();
            }
        }
    }

    /// <summary>
    /// Stops the router like <see cref="Dispose"/> and waits for its owned work to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try { Dispose(); }
        finally
        {
            Task owned;
            lock (this)
            {
                owned = Task.WhenAll([_loops, .. _connects.Keys]);
            }
            await owned.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (owned.IsFaulted)
            {
                logger?.LogWarning(owned.Exception, "Pubsub background work failed");
            }
        }
    }

    private static IReadOnlyDictionary<PeerId, Multiaddress[]> CreateDirectPeers(IEnumerable<Multiaddress>? configuredPeers)
    {
        return (configuredPeers ?? [])
            .Select(address => (PeerId: address.GetPeerId() ?? throw new ArgumentException("A direct peer address must include a peer ID.", nameof(PubsubSettings.DirectPeers)), Address: address))
            .GroupBy(entry => entry.PeerId)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Address).ToArray());
    }

    internal void ConnectDirectPeers(CancellationToken token)
    {
        if (!_settings.AutoConnect || token.IsCancellationRequested) return;
        foreach ((PeerId peerId, Multiaddress[] addresses) in directPeers)
        {
            ConnectionAttempt work;
            lock (this)
            {
                if (IsStopping) return;
                ReconnectionState state = GetReconnectionState(peerId);
                if (state.Disconnecting != 0 || state.Connecting is not null || !NeedsConnection(state)) continue;
                work = BeginConnection(state);
            }
            _ = Connect(work, addresses, token);
        }
    }

    private bool IsDirectPeer(PeerId peerId) => directPeers.ContainsKey(peerId);

    private bool IsOutbound(PeerId peerId) => peerState.GetValueOrDefault(peerId)?.Direction == ConnectionDirection.Outbound;

    private IEnumerable<PeerId> GetGraftCandidates(string topic, HashSet<PeerId> meshPeers,
        HashSet<(string Topic, PeerId Peer)>? prunedThisHeartbeat, bool outboundOnly = false)
    {
        return (gPeers.GetValueOrDefault(topic) ?? [])
            .Where(p => !meshPeers.Contains(p)
                && prunedThisHeartbeat?.Contains((topic, p)) != true
                && !IsDirectPeer(p)
                && (!outboundOnly || IsOutbound(p))
                && GetPeerScoreUnderLock(p) >= 0
                && !IsInBackoff(p, topic));
    }

    private bool IsDirectPeerSubscribedTo(PeerId peerId, string topic)
    {
        return (fPeers.TryGetValue(topic, out HashSet<PeerId>? floodsubPeers) && floodsubPeers.Contains(peerId)) ||
               (gPeers.TryGetValue(topic, out HashSet<PeerId>? gossipsubPeers) && gossipsubPeers.Contains(peerId));
    }

    private IEnumerable<PeerId> GetDirectPeersForTopic(string topic)
    {
        return directPeers.Keys.Where(peerId => IsDirectPeerSubscribedTo(peerId, topic));
    }

    private static void ValidateControlSettings(PubsubSettings settings)
    {
        if (settings.DOut < 0 || settings.DOut > 0 &&
            (settings.DOut >= settings.LowestDegree || settings.DOut > settings.Degree / 2))
        {
            throw new ArgumentOutOfRangeException(nameof(PubsubSettings.DOut), settings.DOut,
                "DOut must be zero or a positive integer below LowestDegree and at most Degree / 2 (integer division).");
        }

        if (settings.MaxIHaveMessages <= 0 || settings.MaxIHaveLength <= 0 ||
            settings.MaxIwantMessages <= 0 || settings.MaxIwantLength <= 0 ||
            settings.GossipRetransmission <= 0 || settings.MaxIwantResponseBytes <= 0 ||
            settings.MaxIwantPromises <= 0 || settings.IWantFollowupTime <= 0 ||
            settings.IdontwantTtlHeartbeats <= 0 || settings.MaxIdontwantMessages <= 0 ||
            settings.MaxIdontwantLength <= 0 || settings.IdontwantMessageThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Gossipsub control limits must be positive.");
        }
    }

    internal int IwantPromiseCount => _iwantPromises.Count;

    internal static T[] Sample<T>(IEnumerable<T> candidates, int count, Random random)
        => SampleOwned(candidates.ToArray(), count, random);

    // Partial Fisher-Yates: choose a uniform subset by shuffling the owned array in place.
    private static T[] SampleOwned<T>(T[] pool, int count, Random random)
    {
        int take = Math.Clamp(count, 0, pool.Length);
        // The final candidate is forced, so a full shuffle needs only n - 1 draws.
        for (int i = 0; i < take && i < pool.Length - 1; i++)
        {
            int j = random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return take == pool.Length ? pool : pool[..take];
    }

    public Task Heartbeat()
    {
        if (_stopped.IsCancellationRequested) return Task.CompletedTask;
        CleanupReconnectionStates();

        // Apply score decay
        DecayScores();

        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
        List<(string Topic, byte[] GroupId, PeerId[] Peers)> partialGossipNotifications = [];
        Action<string, byte[], IReadOnlyList<PeerId>>? onPartialGossip = OnPartialGossip;
        lock (this)
        {
            RemoveExpiredPendingValidations();
            DateTime promiseNow = _timeProvider.GetUtcNow().UtcDateTime;
            IReadOnlyDictionary<PeerId, int> expiredPromises;
            lock (_outboundLock)
            {
                expiredPromises = _iwantPromises.TakeExpired(promiseNow);
                foreach ((PeerId peerId, int brokenPromises) in expiredPromises)
                {
                    if (peerState.TryGetValue(peerId, out PubsubPeer? peer)) peer.Score.AddPenalty(brokenPromises);
                }
            }
            foreach ((PeerId peerId, int brokenPromises) in expiredPromises)
            {
                logger?.LogDebug("Applied behavioral penalty {penalty} to peer {peerId}", brokenPromises, peerId);
            }

            HashSet<(string Topic, PeerId Peer)>? prunedThisHeartbeat = null;
            void QueuePrune(PeerId peerId, ControlPrune prune)
            {
                (prunedThisHeartbeat ??= []).Add((prune.TopicID, peerId));
                peerMessages.GetOrAdd(peerId, _ => new Rpc()).Ensure(r => r.Control.Prune).Add(prune);
            }

            void GraftPeer(string topic, HashSet<PeerId> meshPeers, PeerId peerId)
            {
                meshPeers.Add(peerId);
                RecordPeerJoinMesh(peerId, topic);
                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                    .Ensure(r => r.Control.Graft)
                    .Add(new ControlGraft { TopicID = topic });
            }

            // First, prune peers with negative scores from all meshes (Gossipsub v1.1)
            foreach (KeyValuePair<string, HashSet<PeerId>> meshEntry in mesh)
            {
                string topic = meshEntry.Key;
                var negativePeers = meshEntry.Value.Where(p => GetPeerScoreUnderLock(p) < 0).ToList();
                foreach (var peerId in negativePeers)
                {
                    logger?.LogDebug("Pruning peer {peerId} from mesh for topic {topic} due to negative score", peerId, topic);
                    meshEntry.Value.Remove(peerId);
                    RecordPeerLeaveMesh(peerId, topic);

                    RefreshBackoff(peerId, topic, LocalBackoff(_settings.PruneBackoff));
                    QueuePrune(peerId, CreatePrune(peerId, topic, _settings.PruneBackoff));
                }
            }

            foreach (KeyValuePair<string, HashSet<PeerId>> meshEntry in mesh)
            {
                string topic = meshEntry.Key;
                HashSet<PeerId> meshPeers = meshEntry.Value;
                bool meshEnabled = _settings.HighestDegree > 0;

                if (meshEnabled && meshPeers.Count < _settings.LowestDegree)
                {
                    // Need to graft more peers - exclude peers with negative scores
                    PeerId[] peersToGraft = Sample(GetGraftCandidates(topic, meshPeers, prunedThisHeartbeat),
                        _settings.Degree - meshPeers.Count, _random);

                    foreach (PeerId peerId in peersToGraft)
                    {
                        GraftPeer(topic, meshPeers, peerId);
                    }
                }
                else if (meshPeers.Count > _settings.HighestDegree)
                {
                    // HighestDegree triggers pruning. Degree is the survivor target, capped
                    // by HighestDegree for configurations with inverted bounds.
                    int targetDegree = Math.Clamp(_settings.Degree, 0, Math.Max(0, _settings.HighestDegree));
                    PeerId[] shuffledPeers = Sample(meshPeers, meshPeers.Count, _random);
                    List<PeerId> kept = GetBestScoringPeers(shuffledPeers, Math.Min(_settings.DScore, targetDegree));

                    // Draw independently of the shuffle used to break score ties.
                    int remaining = targetDegree - kept.Count;
                    if (remaining > 0)
                    {
                        kept.AddRange(Sample(meshPeers.Except(kept), remaining, _random));
                    }

                    int missingOutbound = Math.Clamp(_settings.DOut, 0, kept.Count) - kept.Count(IsOutbound);
                    if (missingOutbound > 0)
                    {
                        // Replace random picks before the lowest-scoring protected peers.
                        int replaceIndex = kept.Count - 1;
                        foreach (PeerId outbound in Sample(meshPeers.Where(IsOutbound).Except(kept), missingOutbound, _random))
                        {
                            while (IsOutbound(kept[replaceIndex])) replaceIndex--;
                            kept[replaceIndex--] = outbound;
                        }
                    }

                    // Prune the rest
                    var peersToPrune = meshPeers.Except(kept).ToList();

                    foreach (PeerId peerId in peersToPrune)
                    {
                        meshPeers.Remove(peerId);
                        RecordPeerLeaveMesh(peerId, topic);

                        RefreshBackoff(peerId, topic, LocalBackoff(_settings.PruneBackoff));
                        ControlPrune prune = CreatePrune(peerId, topic, _settings.PruneBackoff);

                        // Only include PX if peer has non-negative score
                        if (peerState.GetValueOrDefault(peerId)?.SupportsPeerExchange is true && GetPeerScoreUnderLock(peerId) >= 0)
                        {
                            prune.Peers.AddRange(meshPeers
                                .Where(pid => GetPeerScoreUnderLock(pid) >= 0)  // Only exchange peers with non-negative scores
                                .Take(_settings.HighestDegree + 2)  // Send more than D_hi for redundancy
                                .Select(pid => (PeerId: pid, Record: _peerStore.TryGetPeerInfo(pid, out var info) ? info.SignedPeerRecord : null))
                                .Where(pid => pid.Record is not null)
                                .Select(pid => new PeerInfo
                                {
                                    PeerID = ByteString.CopyFrom(pid.PeerId.Bytes),
                                    SignedPeerRecord = pid.Record,
                                }));
                        }

                        QueuePrune(peerId, prune);
                    }
                }

                if (meshEnabled && _settings.DOut > 0 && meshPeers.Count >= _settings.LowestDegree)
                {
                    int missingOutbound = _settings.DOut - meshPeers.Count(IsOutbound);
                    if (missingOutbound > 0)
                    {
                        foreach (PeerId peerId in GetGraftCandidates(topic, meshPeers, prunedThisHeartbeat, outboundOnly: true)
                            .Take(missingOutbound).ToArray())
                        {
                            GraftPeer(topic, meshPeers, peerId);
                        }
                    }
                }
            }

            if (_settings.HighestDegree > 0 && _settings.OpportunisticGraftPeers > 0 &&
                (heartbeatTick + 1) % _settings.OpportunisticGraftTicks == 0)
            {
                // Pruning on any topic can lower a peer's total score through its mesh failure penalty.
                // Score peers only after every topic has finished routine maintenance.
                Dictionary<PeerId, double>? opportunisticScores = null;
                foreach ((string topic, HashSet<PeerId> meshPeers) in mesh)
                {
                    if (meshPeers.Count <= 1) continue;

                    opportunisticScores ??= [];

                    foreach (PeerId peerId in GetOpportunisticGraftPeers(topic, meshPeers, opportunisticScores, prunedThisHeartbeat))
                    {
                        GraftPeer(topic, meshPeers, peerId);
                        // Joining can change the score used by a later topic.
                        opportunisticScores.Remove(peerId);
                    }
                }
            }

            foreach (string? fanoutTopic in fanout.Keys.ToArray())
            {
                if (fanoutLastPublished.GetOrAdd(fanoutTopic, _ => DateTime.Now).AddMilliseconds(_settings.FanoutTtl) < DateTime.Now)
                {
                    fanout.Remove(fanoutTopic, out _);
                    fanoutLastPublished.Remove(fanoutTopic, out _);
                }
                else
                {
                    HashSet<PeerId> fanoutPeers = fanout[fanoutTopic];
                    HashSet<PeerId> topicPeers = gPeers.GetValueOrDefault(fanoutTopic) ?? [];
                    fanoutPeers.RemoveWhere(p => !topicPeers.Contains(p) || IsDirectPeer(p) || !MeetsPublishThreshold(p));

                    int peerCountToAdd = _settings.Degree - fanoutPeers.Count;
                    if (peerCountToAdd > 0)
                    {
                        fanoutPeers.UnionWith(Sample(topicPeers.Where(p =>
                            !fanoutPeers.Contains(p) && !IsDirectPeer(p) && MeetsPublishThreshold(p)), peerCountToAdd, _random));
                    }
                }
            }

            foreach (string topic in mesh.Keys.Concat(fanout.Keys).Distinct()
                .ToArray())
            {
                IReadOnlyList<MessageId> messageIds = _messageCache.GetGossipIds(topic);
                bool canGossipPartialMessages =
                    onPartialGossip is not null &&
                    _settings.EnablePartialMessages &&
                    topicState.TryGetValue(topic, out Topic? localTopic) &&
                    localTopic.SupportsSendingPartialMessages;
                IReadOnlyList<byte[]> partialMessageGroupIds = canGossipPartialMessages
                    ? partialMessageGossip.GetGroupIds(topic)
                    : [];
                if (messageIds.Count == 0 && partialMessageGroupIds.Count == 0)
                {
                    continue;
                }

                // Only send gossip to peers above gossip threshold.
                HashSet<PeerId> fanoutPeers = fanout.GetValueOrDefault(topic) ?? [];
                HashSet<PeerId> topicMesh = mesh.GetValueOrDefault(topic) ?? [];
                HashSet<PeerId> topicGossipsubPeers = gPeers.GetValueOrDefault(topic) ?? [];
                PeerId[] eligiblePeers = topicGossipsubPeers
                    .Where(p => !topicMesh.Contains(p)
                        && !fanoutPeers.Contains(p)
                        && !IsDirectPeer(p)
                        && GetPeerScoreUnderLock(p) >= _settings.GossipThreshold)
                    .ToArray();

                // Adaptive gossip: send to gossip_factor of eligible peers (min D_lazy).
                int gossipCount = Math.Max(_settings.LazyDegree, (int)(eligiblePeers.Length * _settings.GossipFactor));
                PeerId[] gossipPeers = SampleOwned(eligiblePeers, gossipCount, _random);

                if (partialMessageGroupIds.Count > 0)
                {
                    PeerId[] partialGossipPeers = gossipPeers
                        .Where(peerId => peerState.TryGetValue(peerId, out PubsubPeer? peer) &&
                            peer.SupportsPartialMessagesExtension &&
                            peer.RequestsPartialMessages(topic))
                        .ToArray();

                    if (partialGossipPeers.Length > 0)
                    {
                        foreach (byte[] groupId in partialMessageGroupIds)
                        {
                            partialGossipNotifications.Add((topic, groupId, partialGossipPeers));
                        }

                        // A group ID does not identify which cached full message it advertises.
                        // Keep IHAVE for those messages so unrelated full messages remain discoverable.
                    }
                }

                if (messageIds.Count != 0)
                {
                    ControlIHave ihave = new() { TopicID = topic };
                    ihave.MessageIDs.AddRange(messageIds.Select(id => ByteString.CopyFrom(id.Bytes)));
                    foreach (PeerId peer in gossipPeers)
                    {
                        peerMessages.GetOrAdd(peer, _ => new Rpc())
                            .Ensure(r => r.Control.Ihave).Add(ihave);
                    }
                }
            }
            partialMessageGossip.Heartbeat();
            _messageCache.Shift();
            heartbeatTick++;
            foreach (PubsubPeer peer in peerState.Values)
            {
                peer.Control.ResetHeartbeatCounters(heartbeatTick);
            }
            foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
            {
                peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
            }
        }

        foreach ((string topic, byte[] groupId, PeerId[] peers) in partialGossipNotifications)
        {
            foreach (Action<string, byte[], IReadOnlyList<PeerId>> handler in onPartialGossip!.GetInvocationList())
            {
                try
                {
                    handler(topic, groupId, peers);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Exception in partial gossip handler for topic {topic}", topic);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void RemovePeer(PeerId peerId, PubsubPeer peer)
    {
        lock (this)
        {
            bool disconnect;
            lock (_outboundLock)
            {
                TerminateUnderLock(peer, disconnect: false);
                disconnect = peer.DisconnectRequired;
            }
            // Scored before removal, as GetPeerScore would, while the peer still counts towards its own colocation.
            double score = ScoreOf(peer);
            if (((ICollection<KeyValuePair<PeerId, PubsubPeer>>)peerState).Remove(new KeyValuePair<PeerId, PubsubPeer>(peerId, peer)))
            {
                UpdateIpColocationCount(peer.Score.IPAddress, -1);
                UpdateIpColocationCount(peer.Score.IPColocationSubnet, -1);
                KeepDisconnectedScore(peerId, peer.Score, score);
                RemoveRemoteSubscriptions(peer);
                foreach (var peersByTopic in new[] { fanout, mesh })
                {
                    foreach (HashSet<PeerId> topicPeers in peersByTopic.Values)
                    {
                        topicPeers.Remove(peerId);
                    }
                }
                if (disconnect)
                {
                    // Dial deduplication can find this session until teardown closes it.
                    peer.RecoveryWaitsForDisconnect = true;
                    peer.Reconnection.Disconnecting++;
                }
                QueueReconnection(peer.Reconnection, PubsubReconnectReason.StreamClosed);
            }
        }
        StopPeer(peer);
    }

    /// <summary>
    /// Cancelled when the router is disposed; streams the router serves close on it.
    /// </summary>
    internal CancellationToken Stopped => _stopped.Token;

    internal CancellationToken LifetimeToken => _lifetimeToken.CanBeCanceled ? _lifetimeToken : Stopped;

    internal PubsubPeer? OutboundConnection(Multiaddress addr, string protocolId, Task dialTask,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IOResult>> write, Func<ValueTask> abort, Func<Task> disconnect,
        ConnectionDirection direction = ConnectionDirection.Unknown)
    {
        PeerId? peerId = addr.GetPeerId();
        lock (this)
        {
            if (peerId is null || _stopped.IsCancellationRequested) return null;
            ReconnectionState state = GetReconnectionState(peerId);
            if (state.Suppressed) return null;
            PubsubPeer peer = GetOrCreatePeer(peerId, addr, protocolId, state, direction, disconnect);
            if (!peer.AttachWriter(write, abort)) return null;
            if (peer.Direction != direction) peer.Direction = ConnectionDirection.Unknown;
            // A pending transport dial still owns its slot until it finishes, even if another path attached the writer.
            if (state.Connecting is { } connecting) connecting.Superseded = true;
            if (state.Retry is { } retry) EndReconnection(retry);
            state.RetryEnded = false;
            if (outboundDials.Remove(peerId, out OutboundDialAttempt? attempt))
            {
                attempt.Reply = null;
                if (attempt.Connection is { } work) ReleaseConnection(work);
            }
            _ = dialTask.ContinueWith(_ => RemovePeer(peerId, peer), TaskScheduler.Default);
            Rpc hello = new();
            hello.Subscriptions.AddRange(topicState.Where(pair => pair.Value.IsSubscribed)
                .Select(pair => CreateSubscription(pair.Key, subscribe: true)));
            if (hello.Subscriptions.Count != 0 || peer.NeedsExtensions) peer.Send(hello);
            return peer;
        }
    }

    internal (CancellationToken Token, Action SuppressReconnection) InboundConnection(Multiaddress addr, string protocolId,
        Task listTask, Func<Task> subDial, Func<Task> disconnect, ConnectionDirection direction = ConnectionDirection.Unknown)
    {
        PeerId? peerId = addr.GetPeerId();
        PubsubPeer peer;
        TaskCompletionSource? reply = null;
        OutboundDialAttempt? outbound;
        lock (this)
        {
            if (peerId is null || peerId == localPeer?.Identity.PeerId || _stopped.IsCancellationRequested)
                return (Canceled, static () => { });
            ReconnectionState state = GetReconnectionState(peerId);
            if (state.Suppressed) return (Canceled, static () => { });
            peer = GetOrCreatePeer(peerId, addr, protocolId, state, direction, disconnect);
            if (peer.Direction != direction) peer.Direction = ConnectionDirection.Unknown;
            outbound = ReserveOutbound(peerId);
            if (outbound is not null)
            {
                outbound.Owner = peer;
                reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Track(reply.Task);
            }
            else if (!peer.HasWriter && outboundDials.TryGetValue(peerId, out OutboundDialAttempt? pending) &&
                (pending.Reply is null || !ReferenceEquals(pending.Reply.Peer, peer)))
            {
                pending.Reply = new DeferredReply(peer, subDial);
            }
            peer.IncomingStreams++;
            _ = listTask.ContinueWith(_ =>
            {
                lock (this)
                {
                    // Outbound duplicate rejection can close one of several inbound streams.
                    // Keep the established generation until its last incoming direction ends.
                    if (--peer.IncomingStreams == 0) RemovePeer(peerId, peer);
                }
            }, TaskScheduler.Default);
        }
        if (reply is not null) _ = OpenReplyAsync(peer, outbound!, subDial, reply);
        return (peer.TokenSource.Token, () =>
        {
            peer.Reconnection.Suppressed = true;
            SignalReconnect();
        }
        );
    }

    private async Task OpenReplyAsync(PubsubPeer peer, OutboundDialAttempt attempt, Func<Task> subDial, TaskCompletionSource completion)
    {
        bool failed = false;
        try
        {
            if (!IsStopping) await subDial().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            logger?.LogDebug(ex, "Pubsub reply stream failed");
        }
        finally
        {
            ReleaseOutbound(peer.PeerId, attempt, failed);
            completion.TrySetResult();
        }
    }

    private PubsubPeer GetOrCreatePeer(PeerId peerId, Multiaddress addr, string protocolId, ReconnectionState state,
        ConnectionDirection direction, Func<Task> disconnect)
    {
        if (direction == ConnectionDirection.Outbound) state.LastOutboundAddress = addr;
        if (peerState.TryGetValue(peerId, out PubsubPeer? existing)) return existing;
        if (state.Retry is null && state.Connecting is null) state.RetryEnded = false;
        PubsubPeer peer = new(this, peerId, protocolId, _settings, state, disconnect) { Address = addr, Direction = direction };
        (peer.Score.IPAddress, peer.Score.IPColocationSubnet) = IpColocation.GetKeys(addr, _settings.IPColocationFactorWhitelist);
        peer = WithRetainedScore(peer, out RetainedScore? restored);
        peer.DeadlineTimer = _timeProvider.CreateTimer(_ => ExpireOutbound(peer), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        peer.Teardown = TeardownPeerAsync(peer);
        peer.Completion = RunWriterAsync(peer);
        Track(peer.Completion);
        UpdateIpColocationCount(peer.Score.IPAddress, 1);
        UpdateIpColocationCount(peer.Score.IPColocationSubnet, 1);
        peerState[peerId] = peer;
        if (restored is not null) ForgetRetainedScore(peerId, restored);
        return peer;
    }
}
