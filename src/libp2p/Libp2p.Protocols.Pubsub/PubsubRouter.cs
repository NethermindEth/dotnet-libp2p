// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

    class PubsubPeer(PeerId peerId, string protocolId, ILogger? logger, Multiaddress address, ConnectionInitiation initialisedBy)
    {
        public CancellationTokenSource TokenSource { get; init; } = new CancellationTokenSource();
        public PeerId PeerId { get; set; } = peerId;
        public bool IsGossipSub => (Protocol & PubsubProtocol.AnyGossipsub) != PubsubProtocol.None;
        public bool IsFloodSub => Protocol == PubsubProtocol.Floodsub;
        public ConnectionInitiation InititatedBy { get; internal set; } = initialisedBy;
        public Multiaddress Address { get; } = address;

        public enum PubsubProtocol
        {
            None = 0,
            Floodsub = 1,
            GossipsubV10 = 2,
            GossipsubV11 = 4,
            GossipsubV12 = 8,
            GossipsubV13 = 16,
            GossipsubV14 = 32,
            AnyGossipsub = GossipsubV10 | GossipsubV11 | GossipsubV12 | GossipsubV13 | GossipsubV14,
        }

        public void Send(Rpc rpc)
        {
            SendRpcQueue.Enqueue(rpc);
            if (SendRpc is not null)
            {
                lock (SendRpcQueue)
                {
                    while (SendRpcQueue.TryDequeue(out Rpc? rpcToSend))
                    {
                        SendRpc.Invoke(rpcToSend);
                    }
                }
            }
        }
        public Dictionary<string, DateTime> Backoff { get; internal set; } = [];
        public ConcurrentQueue<Rpc> SendRpcQueue { get; } = new ConcurrentQueue<Rpc>();
        private Action<Rpc>? _sendRpc;
        private readonly ILogger? _logger = logger;

        public Action<Rpc>? SendRpc
        {
            get => _sendRpc; set
            {
                _logger?.LogDebug("Set SENDRPC for {peerId}: {value}", PeerId, value);
                _sendRpc = value;
                if (_sendRpc is not null)
                    lock (SendRpcQueue)
                    {
                        while (SendRpcQueue.TryDequeue(out Rpc? rpcToSend))
                        {
                            _sendRpc.Invoke(rpcToSend);
                        }
                    }
            }
        }

        public PubsubProtocol Protocol { get; set; } = protocolId switch
        {
            GossipsubProtocolVersionV10 => PubsubProtocol.GossipsubV10,
            GossipsubProtocolVersionV11 => PubsubProtocol.GossipsubV11,
            GossipsubProtocolVersionV12 => PubsubProtocol.GossipsubV12,
            _ => PubsubProtocol.Floodsub,
        };
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

    public event Action<string, byte[]>? OnMessage;
    public Func<Message, MessageValidity>? VerifyMessage = null;

    private readonly PubsubSettings _settings;
    private readonly TtlCache<MessageId, MessageWithId> _messageCache;
    private readonly TtlCache<MessageId, MessageWithId> _limboMessageCache;
    private readonly TtlCache<(PeerId, MessageId)> _dontWantMessages;

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
        _messageCache = new(_settings.MessageCacheTtl);
        _limboMessageCache = new(_settings.MessageCacheTtl);
        _dontWantMessages = new(_settings.MessageCacheTtl);
    }

    public Task StartAsync(ILocalPeer localPeer, CancellationToken token = default)
    {
        logger?.LogDebug("Running pubsub for {listenAddresses}", string.Join(",", localPeer.ListenAddresses));

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
        ArgumentNullException.ThrowIfNull(localPeer);

        try
        {
            ISession session = await localPeer.DialAsync(addrs, token);

            if (!peerState.ContainsKey(session.RemoteAddress.Get<P2P>().ToString()))
            {
                string[]? protocols = _peerStore.GetPeerInfo(session.RemoteAddress.GetPeerId()!)?.SupportedProtocols ?? [];
                if (protocols.Contains(GossipsubProtocolVersionV12))
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
                logger?.LogDebug("Dialing ended to {remoteAddress}", session.RemoteAddress);
                if (peerState.TryGetValue(session.RemoteAddress.GetPeerId()!, out PubsubPeer? state) && state.InititatedBy == ConnectionInitiation.Remote)
                {
                    _ = session.DisconnectAsync();
                }
            }
        }
        catch (Exception e)
        {
            logger?.LogDebug("Adding reconnections for {addrs}: {message}", string.Join(",", addrs.Select(a => a.ToString())), e.Message);
            if (reconnect) reconnections.Add(new Reconnection(addrs, _settings.ReconnectionAttempts));
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _messageCache.Dispose();
        _limboMessageCache.Dispose();
    }

    private void Reconnect(CancellationToken token)
    {
        const int MaxParallelReconnections = 5;

        for (int rCount = 0; reconnections.TryTake(out Reconnection? rec) && rCount < MaxParallelReconnections; rCount++)
        {
            logger?.LogDebug("Reconnect to {addrs}", string.Join(",", rec.Addresses.Select(a => a.ToString())));
            _ = Connect(rec.Addresses, token, true).ContinueWith(t =>
            {
                if (t.IsFaulted && rec.Attempts != 1)
                {
                    reconnections.Add(rec with { Attempts = rec.Attempts - 1 });
                }
            }, token);
        }
    }

    public Task Heartbeat()
    {
        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
        lock (this)
        {
            foreach (KeyValuePair<string, HashSet<PeerId>> mesh in mesh)
            {
                if (mesh.Value.Count < _settings.LowestDegree)
                {
                    PeerId[] peersToGraft = [.. gPeers[mesh.Key]
                        .Where(p => !mesh.Value.Contains(p) && (peerState.GetValueOrDefault(p)?.Backoff.TryGetValue(mesh.Key, out DateTime backoff) != true || backoff < DateTime.Now))
                        .Take(_settings.Degree - mesh.Value.Count)];
                    foreach (PeerId peerId in peersToGraft)
                    {
                        mesh.Value.Add(peerId);
                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                            .Ensure(r => r.Control.Graft)
                            .Add(new ControlGraft { TopicID = mesh.Key });
                    }
                }
                else if (mesh.Value.Count > _settings.HighestDegree)
                {
                    PeerId[] peerstoPrune = [.. mesh.Value.Take(mesh.Value.Count - _settings.HighestDegree)];
                    foreach (PeerId? peerId in peerstoPrune)
                    {
                        mesh.Value.Remove(peerId);
                        ControlPrune prune = new() { TopicID = mesh.Key, Backoff = 60 };
                        prune.Peers.AddRange(mesh.Value.ToArray()
                            .Select(pid => (PeerId: pid, Record: _peerStore.GetPeerInfo(pid)?.SignedPeerRecord))
                            .Where(pid => pid.Record is not null)
                            .Select(pid => new PeerInfo
                            {
                                PeerID = ByteString.CopyFrom(pid.PeerId.Bytes),
                                SignedPeerRecord = pid.Record,
                            }));
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
                        foreach (PeerId? peerId in gPeers[fanoutTopic].Where(p => !fanout[fanoutTopic].Contains(p)).Take(peerCountToAdd))
                        {
                            fanout[fanoutTopic].Add(peerId);
                        }
                    }
                }
            }

            IEnumerable<IGrouping<string, MessageWithId>> msgs = _messageCache.ToList().GroupBy(m => m.Message.Topic);

            foreach (string? topic in gPeers.Keys.Concat(fanout.Keys).Distinct().ToArray())
            {
                IGrouping<string, MessageWithId>? msgsInTopic = msgs.FirstOrDefault(mit => mit.Key == topic);
                if (msgsInTopic is not null)
                {
                    ControlIHave ihave = new() { TopicID = topic };
                    ihave.MessageIDs.AddRange(msgsInTopic.Select(m => ByteString.CopyFrom(m.Id.Bytes)));

                    foreach (PeerId? peer in gPeers[topic].Where(p => !mesh[topic].Contains(p) && !fanout[topic].Contains(p)).Take(_settings.LazyDegree))
                    {
                        peerMessages.GetOrAdd(peer, _ => new Rpc())
                            .Ensure(r => r.Control.Ihave).Add(ihave);
                    }
                }
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
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

        PubsubPeer peer = peerState.GetOrAdd(peerId, (id) => new PubsubPeer(peerId, protocolId, logger, addr, ConnectionInitiation.Local) { SendRpc = sendRpc, InititatedBy = ConnectionInitiation.Local });

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

            string[] topics = [.. topicState.Keys];

            if (topics.Length != 0)
            {
                logger?.LogDebug("Topics sent to {peerId}: {topics}", peerId, string.Join(",", topics));

                Rpc helloMessage = new Rpc().WithTopics(topics, []);
                peer.Send(helloMessage);
            }

            logger?.LogDebug("Outbound {peerId}", peerId);
            return peer.TokenSource.Token;
        }
    }

    internal CancellationToken InboundConnection(Multiaddress remoteAddr, string protocolId, Task listTask, Func<Task> subDial)
    {
        PeerId? peerId = remoteAddr.GetPeerId();

        if (peerId is null || peerId == localPeer!.Identity.PeerId)
        {
            return Canceled;
        }

        PubsubPeer? newPeer = null;
        PubsubPeer existingPeer = peerState.GetOrAdd(peerId, (id) => newPeer = new PubsubPeer(peerId, protocolId, logger, remoteAddr, ConnectionInitiation.Remote));
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
                    reconnections.Add(new Reconnection([remoteAddr], _settings.ReconnectionAttempts));
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
