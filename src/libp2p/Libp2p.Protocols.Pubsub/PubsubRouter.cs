// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter : IRoutingStateContainer, IDisposable
{
    static int routerCounter = 0;
    readonly int routerId = Interlocked.Increment(ref routerCounter);

    public override string ToString()
    {
        //{string.Join("|", peerState.Select(x => $"{x.Key}:{x.Value.SendRpc is not null}"))}
        return $"Router#{routerId}: {localPeer?.Identity.PeerId ?? "null"}, " +
            $"peers: {peerState.Count(x => x.Value.SendRpc is not null)}/{peerState.Count} ({string.Join(",", peerState.Keys)}), " +
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

    internal sealed class PubsubPeer
    {
        public PubsubPeer(PeerId peerId, string protocolId, ILogger? logger, PubsubSettings settings)
        {
            PeerId = peerId;
            _logger = logger;
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
            SendRpcQueue = new ConcurrentQueue<Rpc>();
            Score = new PeerScore(settings);
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

        public void Send(Rpc rpc)
        {
            lock (SendRpcQueue)
            {
                rpc = RemoveUnsupportedPartialSubscriptionOptions(rpc);
                rpc = AddExtensionsIfNeeded(rpc);
                SendRpcQueue.Enqueue(rpc);
                if (_sendRpc is not null)
                {
                    while (SendRpcQueue.TryDequeue(out Rpc? rpcToSend))
                    {
                        _sendRpc.Invoke(rpcToSend);
                    }
                }
            }
        }

        private Rpc RemoveUnsupportedPartialSubscriptionOptions(Rpc rpc)
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
        public ConcurrentQueue<Rpc> SendRpcQueue { get; }
        private Action<Rpc>? _sendRpc;
        private readonly ILogger? _logger;
        private readonly bool _advertisesPartialMessages;
        private readonly ConcurrentDictionary<string, PartialMessagesSubscription> _partialMessagesSubscriptions = new();

        private readonly record struct PartialMessagesSubscription(bool RequestsPartialMessages, bool SupportsSendingPartialMessages);

        public bool ExtensionsSent { get; private set; }
        public bool ReceivedFirstRpc { get; set; }
        public bool SupportsPartialMessagesExtension { get; set; }

        public bool NeedsExtensions
        {
            get
            {
                lock (SendRpcQueue)
                {
                    return _advertisesPartialMessages && SupportsExtensions && !ExtensionsSent;
                }
            }
        }

        public void UpdatePartialMessagesSubscription(string topicId, bool requestsPartialMessages, bool supportsSendingPartialMessages)
        {
            _partialMessagesSubscriptions[topicId] = new(requestsPartialMessages, supportsSendingPartialMessages);
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

        private Rpc AddExtensionsIfNeeded(Rpc rpc)
        {
            if (!_advertisesPartialMessages || !SupportsExtensions || ExtensionsSent)
            {
                return rpc;
            }

            Rpc extendedRpc = rpc.Clone();
            extendedRpc.Control ??= new ControlMessage();
            extendedRpc.Control.Extensions ??= new ControlExtensions();
            extendedRpc.Control.Extensions.PartialMessages = true;
            ExtensionsSent = true;
            return extendedRpc;
        }

        public Action<Rpc>? SendRpc
        {
            get
            {
                lock (SendRpcQueue)
                {
                    return _sendRpc;
                }
            }
            set
            {
                lock (SendRpcQueue)
                {
                    _logger?.LogDebug($"Set SENDRPC for {PeerId}: {value}");
                    _sendRpc = value;
                    while (_sendRpc is not null && SendRpcQueue.TryDequeue(out Rpc? rpcToSend))
                    {
                        _sendRpc.Invoke(rpcToSend);
                    }
                }
            }
        }
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId PeerId { get; set; }

        public PubsubProtocol Protocol { get; set; }
        public bool IsGossipSub => (Protocol & PubsubProtocol.AnyGossipsub) != PubsubProtocol.None;
        public bool IsFloodSub => Protocol == PubsubProtocol.Floodsub;
        public bool SupportsPeerExchange => Protocol is PubsubProtocol.GossipsubV11 or PubsubProtocol.GossipsubV12 or PubsubProtocol.GossipsubV13;
        public bool SupportsExtensions => Protocol == PubsubProtocol.GossipsubV13;

        public ConnectionInitiation InitiatedBy { get; internal set; }
        public Multiaddress Address { get; internal set; } = null!;

        // Peer scoring (Gossipsub v1.1)
        public PeerScore Score { get; internal set; }
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
    /// that requested it instead of an IHAVE announcement. Applications can
    /// respond by calling <see cref="SendPartial"/> for the supplied group.
    /// </summary>
    public event Action<string, byte[], IReadOnlyList<PeerId>>? OnPartialGossip;
    public Func<Message, MessageValidity>? VerifyMessage = null;

    internal int MaxRpcBytes => _settings.MaxRpcBytes;

    private readonly PubsubSettings _settings;
    private readonly TtlCache<MessageId, MessageWithId> _messageCache;
    private readonly TtlCache<MessageId, MessageWithId> _limboMessageCache;
    private readonly TtlCache<(PeerId, MessageId)> _idontwantMessages;
    private readonly PartialMessageGossipCache partialMessageGossip;

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

    private readonly ConcurrentBag<Reconnection> reconnections = [];
    private readonly PeerStore _peerStore;
    private readonly IReadOnlyDictionary<PeerId, Multiaddress[]> directPeers;
    private DateTime nextDirectConnectionAttempt;
    private ulong seqNo = 1;

    private record Reconnection(Multiaddress[] Addresses, int Attempts);

    static PubsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public PubsubRouter(PeerStore store, PubsubSettings? settings = null, ILoggerFactory? loggerFactory = default)
    {
        logger = loggerFactory?.CreateLogger("pubsub-router");

        _peerStore = store;
        _settings = settings ?? PubsubSettings.Default;
        if (_settings.DefaultSignaturePolicy is PubsubSettings.SignaturePolicy.StrictNoSign && _settings.GetMessageId == PubsubSettings.ConcatFromAndSeqno)
        {
            throw new InvalidOperationException("StrictNoSign requires a custom GetMessageId function.");
        }

        if (_settings.DirectConnectPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PubsubSettings.DirectConnectPeriod), "DirectConnectPeriod must be positive.");
        }

        directPeers = CreateDirectPeers(_settings.DirectPeers);
        _messageCache = new(_settings.MessageCacheTtl);
        _limboMessageCache = new(_settings.MessageCacheTtl);
        _idontwantMessages = new(_settings.MessageCacheTtl);
        partialMessageGossip = new(_settings.MaxPartialMessageGroupsPerTopic, _settings.PartialMessageGossipTtlHeartbeats);
    }

    public Task StartAsync(ILocalPeer localPeer, CancellationToken token = default)
    {
        logger?.LogDebug($"Running pubsub for {string.Join(",", localPeer.ListenAddresses)}");

        if (this.localPeer is not null)
        {
            throw new InvalidOperationException("Router has been already started");
        }

        this.localPeer = localPeer;

        _peerStore.OnNewPeer += (addrs) =>
        {
            if (addrs.Any(a => a.GetPeerId()! == localPeer.Identity.PeerId))
            {
                return;
            }
            _ = Connect(addrs, token, true);
        };

        foreach (Multiaddress[] directPeerAddresses in directPeers.Values)
        {
            _peerStore.Discover(directPeerAddresses);
        }
        nextDirectConnectionAttempt = DateTime.UtcNow.AddMilliseconds(_settings.DirectConnectPeriod);

        _ = Task.Run(LoopHeartbeat, token);
        _ = Task.Run(LoopReconnect, token);

        logger?.LogInformation("Started");
        return Task.CompletedTask;


        async Task LoopHeartbeat()
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_settings.HeartbeatInterval, token);
                await Heartbeat();
            }
        }

        async Task LoopReconnect()
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_settings.ReconnectionPeriod, token);
                Reconnect(token);
            }
        }
    }

    private async Task Connect(Multiaddress[] addrs, CancellationToken token, bool reconnect = false)
    {
        try
        {
            ILocalPeer peer = localPeer ?? throw new InvalidOperationException("Router has not been started.");
            ISession session = await peer.DialAsync(addrs, token);

            if (!peerState.ContainsKey(session.RemoteAddress.Get<P2P>().ToString()))
            {
                string[]? protocols = _peerStore.GetPeerInfo(session.RemoteAddress.GetPeerId()!)?.SupportedProtocols ?? [];
                if (protocols.Contains(GossipsubProtocolVersionV13))
                {
                    await session.DialAsync<GossipsubProtocolV13>(token);
                }
                else if (protocols.Contains(GossipsubProtocolVersionV12))
                {
                    await session.DialAsync<GossipsubProtocolV12>(token);
                }
                else if (protocols.Contains(GossipsubProtocolVersionV11))
                {
                    await session.DialAsync<GossipsubProtocolV11>(token);
                }
                else if (protocols.Contains(GossipsubProtocolVersionV10))
                {
                    await session.DialAsync<GossipsubProtocol>(token);
                }
                else if (protocols.Contains(FloodsubProtocolVersion))
                {
                    await session.DialAsync<FloodsubProtocol>(token);
                }
                else
                {
                    _ = session.DisconnectAsync();
                    return;
                }
                logger?.LogDebug($"Dialing ended to {session.RemoteAddress}");
                if (peerState.TryGetValue(session.RemoteAddress.GetPeerId()!, out PubsubPeer? state) && state.InitiatedBy == ConnectionInitiation.Remote)
                {
                    _ = session.DisconnectAsync();
                }
            }
        }
        catch (Exception e)
        {
            logger?.LogDebug($"Adding reconnections for {string.Join(",", addrs.Select(a => a.ToString()))}: {e.Message}");
            if (reconnect) reconnections.Add(new Reconnection(addrs, _settings.ReconnectionAttempts));
        }
    }

    public void Dispose()
    {
        _messageCache.Dispose();
        _limboMessageCache.Dispose();
    }

    private void Reconnect(CancellationToken token)
    {
        const int MaxParallelReconnections = 5;

        for (int rCount = 0; reconnections.TryTake(out Reconnection? rec) && rCount < MaxParallelReconnections; rCount++)
        {
            logger?.LogDebug($"Reconnect to {string.Join(",", rec.Addresses.Select(a => a.ToString()))}");
            _ = Connect(rec.Addresses, token, true).ContinueWith(t =>
            {
                if (t.IsFaulted && rec.Attempts != 1)
                {
                    reconnections.Add(rec with { Attempts = rec.Attempts - 1 });
                }
            }, token);
        }

        ReconnectDirectPeers(token);
    }

    private static IReadOnlyDictionary<PeerId, Multiaddress[]> CreateDirectPeers(IEnumerable<Multiaddress>? configuredPeers)
    {
        return (configuredPeers ?? [])
            .Select(address => (PeerId: address.GetPeerId() ?? throw new ArgumentException("A direct peer address must include a peer ID.", nameof(PubsubSettings.DirectPeers)), Address: address))
            .GroupBy(entry => entry.PeerId)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Address).ToArray());
    }

    private void ReconnectDirectPeers(CancellationToken token)
    {
        if (directPeers.Count == 0 || DateTime.UtcNow < nextDirectConnectionAttempt)
        {
            return;
        }

        nextDirectConnectionAttempt = DateTime.UtcNow.AddMilliseconds(_settings.DirectConnectPeriod);
        foreach ((PeerId peerId, Multiaddress[] addresses) in directPeers)
        {
            if (!peerState.ContainsKey(peerId))
            {
                _ = Connect(addresses, token, reconnect: true);
            }
        }
    }

    private bool IsDirectPeer(PeerId peerId) => directPeers.ContainsKey(peerId);

    private bool IsDirectPeerSubscribedTo(PeerId peerId, string topic)
    {
        return (fPeers.TryGetValue(topic, out HashSet<PeerId>? floodsubPeers) && floodsubPeers.Contains(peerId)) ||
               (gPeers.TryGetValue(topic, out HashSet<PeerId>? gossipsubPeers) && gossipsubPeers.Contains(peerId));
    }

    private IEnumerable<PeerId> GetDirectPeersForTopic(string topic)
    {
        return directPeers.Keys.Where(peerId => IsDirectPeerSubscribedTo(peerId, topic));
    }

    public Task Heartbeat()
    {
        // Apply score decay
        DecayScores();

        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
        List<(string Topic, byte[] GroupId, PeerId[] Peers)> partialGossipNotifications = [];
        Action<string, byte[], IReadOnlyList<PeerId>>? onPartialGossip = OnPartialGossip;
        lock (this)
        {
            // First, prune peers with negative scores from all meshes (Gossipsub v1.1)
            foreach (KeyValuePair<string, HashSet<PeerId>> meshEntry in mesh)
            {
                string topic = meshEntry.Key;
                var negativePeers = meshEntry.Value.Where(p => GetPeerScore(p) < 0).ToList();
                foreach (var peerId in negativePeers)
                {
                    logger?.LogDebug("Pruning peer {peerId} from mesh for topic {topic} due to negative score", peerId, topic);
                    meshEntry.Value.Remove(peerId);
                    RecordPeerLeaveMesh(peerId, topic);

                    ControlPrune prune = new() { TopicID = topic, Backoff = (ulong)(_settings.PruneBackoff / 1000) };
                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                         .Ensure(r => r.Control.Prune)
                         .Add(prune);
                }
            }

            foreach (KeyValuePair<string, HashSet<PeerId>> meshEntry in mesh)
            {
                string topic = meshEntry.Key;
                HashSet<PeerId> meshPeers = meshEntry.Value;

                if (meshPeers.Count < _settings.LowestDegree)
                {
                    // Need to graft more peers - exclude peers with negative scores
                    PeerId[] peersToGraft = gPeers[topic]
                        .Where(p => !meshPeers.Contains(p)
                            && !IsDirectPeer(p)
                            && GetPeerScore(p) >= 0  // Only graft non-negative scoring peers
                            && (peerState.GetValueOrDefault(p)?.Backoff.TryGetValue(topic, out DateTime backoff) != true || backoff < DateTime.Now))
                        .Take(_settings.Degree - meshPeers.Count).ToArray();

                    foreach (PeerId peerId in peersToGraft)
                    {
                        meshPeers.Add(peerId);
                        RecordPeerJoinMesh(peerId, topic);
                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                            .Ensure(r => r.Control.Graft)
                            .Add(new ControlGraft { TopicID = topic });
                    }
                }
                else if (meshPeers.Count > _settings.HighestDegree)
                {
                    // Need to prune - keep best scoring peers (Gossipsub v1.1)
                    int numToPrune = meshPeers.Count - _settings.HighestDegree;

                    // Get D_score best peers
                    var bestPeers = GetBestScoringPeers(meshPeers, _settings.DScore).ToHashSet();

                    // Ensure we have D_out outbound connections
                    var outboundPeers = meshPeers
                        .Where(p => peerState.GetValueOrDefault(p)?.InitiatedBy == ConnectionInitiation.Local)
                        .Take(_settings.DOut)
                        .ToHashSet();

                    // Peers to keep: best scores + outbound quota + random
                    var peersToKeep = bestPeers.Union(outboundPeers).ToHashSet();

                    // Fill remaining spots randomly
                    int remaining = _settings.HighestDegree - peersToKeep.Count;
                    if (remaining > 0)
                    {
                        var candidates = meshPeers.Except(peersToKeep).ToList();
                        foreach (var peer in candidates.OrderBy(_ => Random.Shared.Next()).Take(remaining))
                        {
                            peersToKeep.Add(peer);
                        }
                    }

                    // Prune the rest
                    var peersToPrune = meshPeers.Except(peersToKeep).ToList();

                    foreach (PeerId peerId in peersToPrune)
                    {
                        meshPeers.Remove(peerId);
                        RecordPeerLeaveMesh(peerId, topic);

                        ControlPrune prune = new() { TopicID = topic, Backoff = (ulong)(_settings.PruneBackoff / 1000) };

                        // Only include PX if peer has non-negative score
                        if (GetPeerScore(peerId) >= 0)
                        {
                            prune.Peers.AddRange(meshPeers
                                .Where(pid => GetPeerScore(pid) >= 0)  // Only exchange peers with non-negative scores
                                .Take(_settings.HighestDegree + 2)  // Send more than D_hi for redundancy
                                .Select(pid => (PeerId: pid, Record: _peerStore.GetPeerInfo(pid)?.SignedPeerRecord))
                                .Where(pid => pid.Record is not null)
                                .Select(pid => new PeerInfo
                                {
                                    PeerID = ByteString.CopyFrom(pid.PeerId.Bytes),
                                    SignedPeerRecord = pid.Record,
                                }));
                        }

                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                             .Ensure(r => r.Control.Prune)
                             .Add(prune);
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
                    int peerCountToAdd = _settings.Degree - fanout[fanoutTopic].Count;
                    if (peerCountToAdd > 0)
                    {
                        foreach (PeerId? peerId in gPeers[fanoutTopic].Where(p => !fanout[fanoutTopic].Contains(p) && !IsDirectPeer(p)).Take(peerCountToAdd))
                        {
                            fanout[fanoutTopic].Add(peerId);
                        }
                    }
                }
            }

            IEnumerable<IGrouping<string, MessageWithId>> msgs = _messageCache.ToList().GroupBy(m => m.Message.Topic);

            foreach (string topic in gPeers.Keys.Concat(fanout.Keys).Distinct()
                .Where(topic => topicState.GetValueOrDefault(topic)?.IsSubscribed is true || fanout.ContainsKey(topic))
                .ToArray())
            {
                IGrouping<string, MessageWithId>? msgsInTopic = msgs.FirstOrDefault(mit => mit.Key == topic);
                bool canGossipPartialMessages =
                    onPartialGossip is not null &&
                    _settings.EnablePartialMessages &&
                    topicState.TryGetValue(topic, out Topic? localTopic) &&
                    localTopic.SupportsSendingPartialMessages;
                IReadOnlyList<byte[]> partialMessageGroupIds = canGossipPartialMessages
                    ? partialMessageGossip.GetGroupIds(topic)
                    : [];
                if (msgsInTopic is null && partialMessageGroupIds.Count == 0)
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
                        && GetPeerScore(p) >= _settings.GossipThreshold)
                    .ToArray();

                // Adaptive gossip: send to gossip_factor of eligible peers (min D_lazy).
                int gossipCount = Math.Max(_settings.LazyDegree, (int)(eligiblePeers.Length * _settings.GossipFactor));
                PeerId[] gossipPeers = eligiblePeers.Take(gossipCount).ToArray();

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

                        gossipPeers = gossipPeers.Except(partialGossipPeers).ToArray();
                    }
                }

                if (msgsInTopic is not null)
                {
                    ControlIHave ihave = new() { TopicID = topic };
                    ihave.MessageIDs.AddRange(msgsInTopic.Select(m => ByteString.CopyFrom(m.Id.Bytes)));
                    foreach (PeerId peer in gossipPeers)
                    {
                        peerMessages.GetOrAdd(peer, _ => new Rpc())
                            .Ensure(r => r.Control.Ihave).Add(ihave);
                    }
                }
            }

            partialMessageGossip.Heartbeat();
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
        }

        foreach ((string topic, byte[] groupId, PeerId[] peers) in partialGossipNotifications)
        {
            onPartialGossip?.Invoke(topic, groupId, peers);
        }

        return Task.CompletedTask;
    }

    internal CancellationToken OutboundConnection(Multiaddress addr, string protocolId, Task dialTask, Action<Rpc> sendRpc)
    {
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null)
        {
            return Canceled;
        }

        PubsubPeer peer = peerState.GetOrAdd(peerId, (id) => new PubsubPeer(peerId, protocolId, logger, _settings) { Address = addr, SendRpc = sendRpc, InitiatedBy = ConnectionInitiation.Local });

        lock (peer)
        {
            if (peer.SendRpc != sendRpc)
            {
                if (peer.SendRpc is null)
                {
                    peer.SendRpc = sendRpc;
                }
                else
                {
                    logger?.LogDebug("Outbound, rpc set for {peerId}, cancelling", peerId);
                    return Canceled;
                }
            }


            logger?.LogDebug("Outbound, let's dial {peerId} via remotely initiated connection", peerId);

            dialTask.ContinueWith(t =>
            {
                peerState.GetValueOrDefault(peerId)?.TokenSource.Cancel();
                peerState.TryRemove(peerId, out _);
                foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in fPeers)
                {
                    topicPeers.Value.Remove(peerId);
                }
                foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in gPeers)
                {
                    topicPeers.Value.Remove(peerId);
                }
                foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in fanout)
                {
                    topicPeers.Value.Remove(peerId);
                }
                foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in mesh)
                {
                    topicPeers.Value.Remove(peerId);
                }
                reconnections.Add(new Reconnection([addr], _settings.ReconnectionAttempts));
            });

            string[] topics;
            lock (this)
            {
                topics = topicState
                    .Where(pair => pair.Value.IsSubscribed)
                    .Select(pair => pair.Key)
                    .ToArray();
            }

            if (topics.Any() || peer.NeedsExtensions)
            {
                logger?.LogDebug("Topics sent to {peerId}: {topics}", peerId, string.Join(",", topics));

                Rpc helloMessage = new();
                helloMessage.Subscriptions.AddRange(topics.Select(topic => CreateSubscription(topic, subscribe: true)));
                peer.Send(helloMessage);
            }

            logger?.LogDebug("Outbound {peerId}", peerId);
            return peer.TokenSource.Token;
        }
    }

    internal CancellationToken InboundConnection(Multiaddress addr, string protocolId, Task listTask, Action subDial)
    {
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null || peerId == localPeer!.Identity.PeerId)
        {
            return Canceled;
        }

        PubsubPeer? newPeer = null;
        PubsubPeer existingPeer = peerState.GetOrAdd(peerId, (id) => newPeer = new PubsubPeer(peerId, protocolId, logger, _settings) { Address = addr, InitiatedBy = ConnectionInitiation.Remote });
        lock (existingPeer)
        {

            if (newPeer is not null)
            {
                logger?.LogDebug("Inbound, let's dial {peerId} via remotely initiated connection", peerId);
                listTask.ContinueWith(t =>
                {
                    peerState.GetValueOrDefault(peerId)?.TokenSource.Cancel();
                    peerState.TryRemove(peerId, out _);
                    foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in fPeers)
                    {
                        topicPeers.Value.Remove(peerId);
                    }
                    foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in gPeers)
                    {
                        topicPeers.Value.Remove(peerId);
                    }
                    foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in fanout)
                    {
                        topicPeers.Value.Remove(peerId);
                    }
                    foreach (KeyValuePair<string, HashSet<PeerId>> topicPeers in mesh)
                    {
                        topicPeers.Value.Remove(peerId);
                    }
                    reconnections.Add(new Reconnection([addr], _settings.ReconnectionAttempts));
                });

                subDial();
                return newPeer.TokenSource.Token;
            }
            else
            {
                return existingPeer.TokenSource.Token;
            }
        }
    }
}

internal enum ConnectionInitiation
{
    Local,
    Remote,
}

internal readonly struct MessageWithId(MessageId id, Message message)
{
    public MessageId Id { get; } = id;
    public Message Message { get; } = message;
}
