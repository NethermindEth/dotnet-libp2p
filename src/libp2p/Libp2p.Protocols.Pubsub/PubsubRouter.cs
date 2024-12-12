// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public interface IRoutingStateContainer
{
    ConcurrentDictionary<string, HashSet<PeerId>> FloodsubPeers { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> GossipsubPeers { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> Mesh { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> Fanout { get; }
    ConcurrentDictionary<string, DateTime> FanoutLastPublished { get; }
    ICollection<PeerId> ConnectedPeers { get; }
    bool Started { get; }
    Task Heartbeat();
}

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

    class PubsubPeer
    {
        public PubsubPeer(PeerId peerId, string protocolId, ILogger? logger)
        {
            PeerId = peerId;
            _logger = logger;
            Protocol = protocolId switch
            {
                GossipsubProtocolVersionV10 => PubsubProtocol.GossipsubV10,
                GossipsubProtocolVersionV11 => PubsubProtocol.GossipsubV11,
                GossipsubProtocolVersionV12 => PubsubProtocol.GossipsubV12,
                _ => PubsubProtocol.Floodsub,
            };
            TokenSource = new CancellationTokenSource();
            Backoff = [];
            SendRpcQueue = new ConcurrentQueue<Rpc>();
        }

        public enum PubsubProtocol
        {
            None = 0,
            Floodsub = 1,
            GossipsubV10 = 2,
            GossipsubV11 = 4,
            GossipsubV12 = 8,
            AnyGossipsub = GossipsubV10 | GossipsubV11 | GossipsubV12,
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
        public Dictionary<string, DateTime> Backoff { get; internal set; }
        public ConcurrentQueue<Rpc> SendRpcQueue { get; }
        private Action<Rpc>? _sendRpc;
        private readonly ILogger? _logger;

        public Action<Rpc>? SendRpc
        {
            get => _sendRpc; set
            {
                _logger?.LogDebug($"Set SENDRPC for {PeerId}: {value}");
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
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId PeerId { get; set; }

        public PubsubProtocol Protocol { get; set; }
        public bool IsGossipSub => (Protocol & PubsubProtocol.AnyGossipsub) != PubsubProtocol.None;
        public bool IsFloodSub => Protocol == PubsubProtocol.Floodsub;

        public ConnectionInitiation InititatedBy { get; internal set; }
        public Multiaddress Address { get; internal set; }
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
    private readonly TtlCache<MessageId, Message> _messageCache;
    private readonly TtlCache<MessageId, Message> _limboMessageCache;
    private readonly TtlCache<(PeerId, MessageId)> _dontWantMessages;

    private IPeer? localPeer;
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

    public async Task StartAsync(IPeer localPeer, CancellationToken token = default)
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
            _ = Task.Run(async () =>
            {
                try
                {
                    ISession session = await localPeer.DialAsync(addrs, token);

                    if (!peerState.ContainsKey(session.RemoteAddress.Get<P2P>().ToString()))
                    {
                        await session.DialAsync<GossipsubProtocolV11>(token);
                        if (peerState.TryGetValue(session.RemoteAddress.GetPeerId()!, out PubsubPeer? state) && state.InititatedBy == ConnectionInitiation.Remote)
                        {
                            _ = session.DisconnectAsync();
                        }
                    }
                }
                catch
                {
                    reconnections.Add(new Reconnection(addrs, this._settings.ReconnectionAttempts));
                }
            });
        };

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(this._settings.HeartbeatInterval);
                await Heartbeat();
            }
        }, token);

        // reconnection if needed
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(this._settings.ReconnectionPeriod);
                await Reconnect(token);
            }
        }, token);

        logger?.LogInformation("Started");
    }

    public void Dispose()
    {
        _messageCache.Dispose();
        _limboMessageCache.Dispose();
    }

    private async Task Reconnect(CancellationToken token)
    {
        while (reconnections.TryTake(out Reconnection? rec))
        {
            try
            {
                ISession remotePeer = await localPeer.DialAsync(rec.Addresses, token);
                await remotePeer.DialAsync<GossipsubProtocol>(token);
            }
            catch
            {
                if (rec.Attempts != 1)
                {
                    reconnections.Add(rec with { Attempts = rec.Attempts - 1 });
                }
            }
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
                    PeerId[] peersToGraft = gPeers[mesh.Key]
                        .Where(p => !mesh.Value.Contains(p) && (peerState.GetValueOrDefault(p)?.Backoff.TryGetValue(mesh.Key, out DateTime backoff) != true || backoff < DateTime.Now))
                        .Take(_settings.Degree - mesh.Value.Count).ToArray();
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
                    PeerId[] peerstoPrune = mesh.Value.Take(mesh.Value.Count - _settings.HighestDegree).ToArray();
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

            IEnumerable<IGrouping<string, Message>> msgs = _messageCache.ToList().GroupBy(m => m.Topic);

            foreach (string? topic in gPeers.Keys.Concat(fanout.Keys).Distinct().ToArray())
            {
                IGrouping<string, Message>? msgsInTopic = msgs.FirstOrDefault(mit => mit.Key == topic);
                if (msgsInTopic is not null)
                {
                    ControlIHave ihave = new() { TopicID = topic };
                    ihave.MessageIDs.AddRange(msgsInTopic.Select(m => ByteString.CopyFrom(_settings.GetMessageId(m).Bytes)));

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

        PubsubPeer peer = peerState.GetOrAdd(peerId, (id) => new PubsubPeer(peerId, protocolId, logger) { Address = addr, SendRpc = sendRpc, InititatedBy = ConnectionInitiation.Local });

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

            string[] topics = topicState.Keys.ToArray();

            if (topics.Any())
            {
                logger?.LogDebug("Topics sent to {peerId}: {topics}", peerId, string.Join(",", topics));

                Rpc helloMessage = new Rpc().WithTopics(topics, []);
                peer.Send(helloMessage);
            }

            logger?.LogDebug("Outbound {peerId}", peerId);
            return peer.TokenSource.Token;
        }
    }

    internal CancellationToken InboundConnection(Multiaddress addr, string protocolId, Task listTask, Task dialTask, Func<Task> subDial)
    {
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null || peerId == localPeer!.Identity.PeerId)
        {
            return Canceled;
        }

        PubsubPeer? newPeer = null;
        PubsubPeer existingPeer = peerState.GetOrAdd(peerId, (id) => newPeer = new PubsubPeer(peerId, protocolId, logger) { Address = addr, InititatedBy = ConnectionInitiation.Remote });
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

    internal async Task OnRpc(PeerId peerId, Rpc rpc)
    {
        try
        {
            ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
            lock (this)
            {
                if (rpc.Publish.Any())
                {
                    logger?.LogDebug($"Messages received: {rpc.Publish.Select(_settings.GetMessageId).Count(messageId => _limboMessageCache.Contains(messageId) || _messageCache!.Contains(messageId))}/{rpc.Publish.Count}: {rpc.Publish.Count}");

                    foreach (Message? message in rpc.Publish)
                    {
                        MessageId messageId = _settings.GetMessageId(message);

                        if (_limboMessageCache.Contains(messageId) || _messageCache!.Contains(messageId))
                        {
                            continue;
                        }

                        switch (VerifyMessage?.Invoke(message))
                        {
                            case MessageValidity.Rejected:
                            case MessageValidity.Ignored:
                                _limboMessageCache.Add(messageId, message);
                                continue;
                            case MessageValidity.Trottled:
                                continue;
                        }

                        if (!message.VerifySignature(_settings.DefaultSignaturePolicy))
                        {
                            _limboMessageCache!.Add(messageId, message);
                            continue;
                        }

                        _messageCache.Add(messageId, message);

                        PeerId author = new(message.From.ToArray());
                        OnMessage?.Invoke(message.Topic, message.Data.ToByteArray());

                        if (fPeers.TryGetValue(message.Topic, out HashSet<PeerId>? topicPeers))
                        {
                            foreach (PeerId peer in topicPeers)
                            {
                                if (peer == author || peer == peerId)
                                {
                                    continue;
                                }
                                peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                            }
                        }
                        if (mesh.TryGetValue(message.Topic, out topicPeers))
                        {
                            foreach (PeerId peer in topicPeers)
                            {
                                if (peer == author || peer == peerId)
                                {
                                    continue;
                                }
                                peerMessages.GetOrAdd(peer, _ => new Rpc()).Publish.Add(message);
                            }
                        }
                    }
                }

                if (rpc.Subscriptions.Any())
                {
                    foreach (Rpc.Types.SubOpts? sub in rpc.Subscriptions)
                    {
                        PubsubPeer? state = peerState.GetValueOrDefault(peerId);
                        if (state is null)
                        {
                            return;
                        }
                        if (sub.Subscribe)
                        {
                            if (state.IsGossipSub)
                            {
                                gPeers.GetOrAdd(sub.Topicid, _ => []).Add(peerId);
                            }
                            else if (state.IsFloodSub)
                            {
                                fPeers.GetOrAdd(sub.Topicid, _ => []).Add(peerId);
                            }
                        }
                        else
                        {
                            if (state.IsGossipSub)
                            {
                                gPeers.GetOrAdd(sub.Topicid, _ => []).Remove(peerId);
                                if (mesh.ContainsKey(sub.Topicid))
                                {
                                    mesh[sub.Topicid].Remove(peerId);
                                }
                                if (fanout.ContainsKey(sub.Topicid))
                                {
                                    fanout[sub.Topicid].Remove(peerId);
                                }
                            }
                            else if (state.IsFloodSub)
                            {
                                fPeers.GetOrAdd(sub.Topicid, _ => []).Remove(peerId);
                            }
                        }
                    }
                }

                if (rpc.Control is not null)
                {
                    if (rpc.Control.Graft.Any())
                    {
                        foreach (ControlGraft? graft in rpc.Control.Graft)
                        {
                            if (!topicState.ContainsKey(graft.TopicID))
                            {
                                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                    .Ensure(r => r.Control.Prune)
                                    .Add(new ControlPrune { TopicID = graft.TopicID });
                            }
                            else
                            {
                                HashSet<PeerId> topicMesh = mesh[graft.TopicID];

                                if (topicMesh.Count >= _settings.HighestDegree)
                                {
                                    ControlPrune prune = new() { TopicID = graft.TopicID };

                                    if (peerState.TryGetValue(peerId, out PubsubPeer? state) && state.IsGossipSub && state.Protocol >= PubsubPeer.PubsubProtocol.GossipsubV11)
                                    {
                                        state.Backoff[prune.TopicID] = DateTime.Now.AddSeconds(prune.Backoff == 0 ? 60 : prune.Backoff);
                                        prune.Peers.AddRange(topicMesh.ToArray().Select(pid => (PeerId: pid, Record: _peerStore.GetPeerInfo(pid)?.SignedPeerRecord)).Where(pid => pid.Record is not null).Select(pid => new PeerInfo
                                        {
                                            PeerID = ByteString.CopyFrom(pid.PeerId.Bytes),
                                            SignedPeerRecord = pid.Record,
                                        }));
                                    }

                                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                        .Ensure(r => r.Control.Prune)
                                        .Add(prune);
                                }
                                else
                                {
                                    if (!topicMesh.Contains(peerId))
                                    {
                                        topicMesh.Add(peerId);
                                        gPeers[graft.TopicID].Add(peerId);
                                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                            .Ensure(r => r.Control.Graft)
                                            .Add(new ControlGraft { TopicID = graft.TopicID });
                                    }
                                }
                            }
                        }
                    }

                    if (rpc.Control.Prune.Any())
                    {
                        foreach (ControlPrune? prune in rpc.Control.Prune)
                        {
                            if (topicState.ContainsKey(prune.TopicID) && mesh[prune.TopicID].Contains(peerId))
                            {
                                if (peerState.TryGetValue(peerId, out PubsubPeer? state))
                                {
                                    state.Backoff[prune.TopicID] = DateTime.Now.AddSeconds(prune.Backoff == 0 ? 60 : prune.Backoff);
                                }
                                mesh[prune.TopicID].Remove(peerId);
                                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                    .Ensure(r => r.Control.Prune)
                                    .Add(new ControlPrune { TopicID = prune.TopicID });

                                foreach (PeerInfo? peer in prune.Peers)
                                {
                                    _peerStore.Discover(peer.SignedPeerRecord);
                                }
                            }
                        }
                    }

                    if (rpc.Control.Ihave.Any())
                    {
                        List<MessageId> messageIds = [];

                        foreach (ControlIHave? ihave in rpc.Control.Ihave
                            .Where(iw => topicState.ContainsKey(iw.TopicID)))
                        {
                            messageIds.AddRange(ihave.MessageIDs.Select(m => new MessageId(m.ToByteArray()))
                                .Where(mid => !_messageCache.Contains(mid)));
                        }

                        if (messageIds.Any())
                        {
                            ControlIWant ciw = new();
                            foreach (MessageId mId in messageIds)
                            {
                                ciw.MessageIDs.Add(ByteString.CopyFrom(mId.Bytes));
                            }
                            peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                .Ensure(r => r.Control.Iwant)
                                .Add(ciw);
                        }
                    }

                    if (rpc.Control.Iwant.Any())
                    {
                        IEnumerable<MessageId> messageIds = rpc.Control.Iwant.SelectMany(iw => iw.MessageIDs).Select(m => new MessageId(m.ToByteArray()));
                        List<Message> messages = [];
                        foreach (MessageId? mId in messageIds)
                        {
                            Message message = _messageCache.Get(mId);
                            if (message != default)
                            {
                                messages.Add(message);
                            }
                        }
                        if (messages.Any())
                        {
                            peerMessages.GetOrAdd(peerId, _ => new Rpc())
                               .Publish.AddRange(messages);
                        }
                    }

                    if (rpc.Control.Idontwant.Any())
                    {
                        foreach (MessageId messageId in rpc.Control.Iwant.SelectMany(iw => iw.MessageIDs).Select(m => new MessageId(m.ToByteArray())).Take(_settings.MaxIdontwantMessages))
                        {
                            _dontWantMessages.Add((peerId, messageId));
                        }
                    }
                }
            }
            foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
            {
                peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Exception while processing RPC");
        }
    }
}

internal enum ConnectionInitiation
{
    Local,
    Remote,
}
