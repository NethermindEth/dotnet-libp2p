// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal interface IRoutingStateContainer
{
    ConcurrentDictionary<string, HashSet<PeerId>> FloodsubPeers { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> GossipsubPeers { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> Mesh { get; }
    ConcurrentDictionary<string, HashSet<PeerId>> Fanout { get; }
    ConcurrentDictionary<string, DateTime> FanoutLastPublished { get; }
    ICollection<PeerId> ConnectedPeers { get; }
    Task Heartbeat();
}

public class PubsubRouter(ILoggerFactory? loggerFactory = default) : IRoutingStateContainer
{
    public const string FloodsubProtocolVersion = "/floodsub/1.0.0";
    public const string GossipsubProtocolVersionV10 = "/meshsub/1.0.0";
    public const string GossipsubProtocolVersionV11 = "/meshsub/1.1.0";
    public const string GossipsubProtocolVersionV12 = "/meshsub/1.2.0";

    class PubsubPeer
    {
        public PubsubPeer(PeerId peerId, string protocolId)
        {
            PeerId = peerId;
            Protocol = protocolId switch
            {
                FloodsubProtocolVersion => PubsubProtocol.Floodsub,
                _ => PubsubProtocol.Gossipsub,
            };
            TokenSource = new CancellationTokenSource();
            Backoff = [];
            SendRpcQueue = new ConcurrentQueue<Rpc>();
        }

        private enum PubsubProtocol
        {
            Unknown,
            Floodsub,
            Gossipsub,
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
        public Action<Rpc>? SendRpc
        {
            get => _sendRpc; set
            {
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

        private PubsubProtocol Protocol { get; set; }
        public bool IsGossipSub => Protocol == PubsubProtocol.Gossipsub;
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
    ICollection<PeerId> IRoutingStateContainer.ConnectedPeers => peerState.Keys;
    Task IRoutingStateContainer.Heartbeat() => Heartbeat();
    #endregion

    public PeerId? LocalPeerId { get; private set; }

    public event Action<string, byte[]>? OnMessage;
    public Func<Message, MessageValidity>? VerifyMessage = null;

    private Settings settings;
    private TtlCache<MessageId, Message> messageCache;
    private TtlCache<MessageId, Message> limboMessageCache;
    private TtlCache<(PeerId, MessageId)> dontWantMessages;

    private IPeer? localPeer;
    private ManagedPeer peer;
    private ILogger? logger = loggerFactory?.CreateLogger<PubsubRouter>();

    // all floodsub peers in topics
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> fPeers = new();
    // all gossipsub peers in topics
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> gPeers = new();

    // gossip peers in mesh, which is subnet for message exchange 
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> mesh = new();

    // gossip peers in mesh, which is subnet for message exchange for topics that we did not subscribe to
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> fanout = new();
    private readonly ConcurrentDictionary<string, DateTime> fanoutLastPublished = new();


    // all peers with their connection status
    private readonly ConcurrentDictionary<PeerId, PubsubPeer> peerState = new();
    private readonly ConcurrentDictionary<string, Topic> topicState = new();
    private readonly ConcurrentBag<Reconnection> reconnections = new();
    private ulong seqNo = 1;

    private record Reconnection(Multiaddress[] Addresses, int Attempts);

    static PubsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public async Task RunAsync(IPeer localPeer, IDiscoveryProtocol discoveryProtocol, Settings? settings = null, CancellationToken token = default)
    {
        if (this.localPeer is not null)
        {
            throw new InvalidOperationException("Router has been already started");
        }
        this.localPeer = localPeer;
        peer = new ManagedPeer(localPeer);
        this.settings = settings ?? Settings.Default;
        messageCache = new(this.settings.MessageCacheTtl);
        limboMessageCache = new(this.settings.MessageCacheTtl);
        dontWantMessages = new(this.settings.MessageCacheTtl);

        LocalPeerId = new PeerId(localPeer.Address.Get<P2P>().ToString()!);

        _ = localPeer.ListenAsync(localPeer.Address, token);
        _ = StartDiscoveryAsync(discoveryProtocol, token);
        logger?.LogInformation("Started");

        // reconnection if needed
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(this.settings.ReconnectionPeriod);
                await Reconnect(token);
            }
        }, token);

        await Task.Delay(Timeout.Infinite, token);
        messageCache.Dispose();
        limboMessageCache.Dispose();
    }

    private async Task StartDiscoveryAsync(IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(localPeer);

        ObservableCollection<Multiaddress> col = [];
        discoveryProtocol.OnAddPeer = (addrs) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    ISession session = await peer.DialAsync(addrs, token);

                    if (!peerState.ContainsKey(session.Address.Get<P2P>().ToString()))
                    {
                        await session.DialAsync<GossipsubProtocol>(token);
                        if (peerState.TryGetValue(session.Address.GetPeerId()!, out PubsubPeer? state) && state.InititatedBy == ConnectionInitiation.Remote)
                        {
                            _ = session.DisconnectAsync();
                        }
                    }
                }
                catch
                {
                    reconnections.Add(new Reconnection(addrs, settings.ReconnectionAttempts));
                }
            });
            return true;
        };

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(settings.HeartbeatInterval);
                await Heartbeat();
            }
        }, token);

        await discoveryProtocol.DiscoverAsync(localPeer.Address, token);
    }

    private async Task Reconnect(CancellationToken token)
    {
        while (reconnections.TryTake(out Reconnection? rec))
        {
            try
            {
                ISession remotePeer = await peer.DialAsync(rec.Addresses, token);
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

    public ITopic Subscribe(string topicId)
    {
        Topic topic = topicState.GetOrAdd(topicId, (tId) => new(this, tId));

        if (!fPeers.TryAdd(topicId, []))
        {
            // Already exists
            return topic;
        }

        gPeers.TryAdd(topicId, []);

        mesh.TryAdd(topicId, []);

        Rpc topicUpdate = new Rpc().WithTopics([topicId], []);
        foreach (var peer in peerState)
        {
            peer.Value.Send(topicUpdate);
        }

        return topic;
    }

    public void Unsubscribe(string topicId)
    {
        foreach (PeerId peerId in fPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics([], [topicId]);

            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }
        foreach (PeerId peerId in gPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics([], [topicId]);

            if (mesh.TryGetValue(topicId, out HashSet<PeerId>? topicMesh) && topicMesh.Contains(peerId))
            {
                msg.Ensure(r => r.Control.Prune).Add(new ControlPrune { TopicID = topicId });
            }
            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }
    }

    public void UnsubscribeAll()
    {
        foreach (PeerId? peerId in fPeers.SelectMany(kv => kv.Value))
        {
            Rpc msg = new Rpc()
                .WithTopics(Enumerable.Empty<string>(), topicState.Keys);

            peerState.GetValueOrDefault(peerId)?.Send(msg);
        }
        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();

        foreach (PeerId? peerId in gPeers.SelectMany(kv => kv.Value))
        {
            peerMessages.GetOrAdd(peerId, _ => new Rpc())
                .WithTopics(Enumerable.Empty<string>(), topicState.Keys);
        }

        foreach (KeyValuePair<string, HashSet<PeerId>> topicMesh in mesh)
        {
            foreach (PeerId peerId in topicMesh.Value)
            {
                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                    .Ensure(r => r.Control.Prune)
                    .Add(new ControlPrune { TopicID = topicMesh.Key });
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState.GetValueOrDefault(peerMessage.Key)?.Send(peerMessage.Value);
        }
    }

    public Task Heartbeat()
    {
        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
        lock (this)
        {

            foreach (KeyValuePair<string, HashSet<PeerId>> mesh in mesh)
            {
                if (mesh.Value.Count < settings.LowestDegree)
                {
                    PeerId[] peersToGraft = gPeers[mesh.Key]
                        .Where(p => !mesh.Value.Contains(p)
                        && (peerState.GetValueOrDefault(p)?.Backoff.TryGetValue(mesh.Key, out DateTime backoff) != true ||
                            backoff < DateTime.Now)).Take(settings.Degree - mesh.Value.Count).ToArray();
                    foreach (PeerId peerId in peersToGraft)
                    {
                        mesh.Value.Add(peerId);
                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                            .Ensure(r => r.Control.Graft)
                            .Add(new ControlGraft { TopicID = mesh.Key });
                    }
                }
                else if (mesh.Value.Count > settings.HighestDegree)
                {
                    PeerId[] peerstoPrune = mesh.Value.Take(mesh.Value.Count - settings.HighestDegree).ToArray();
                    foreach (PeerId? peerId in peerstoPrune)
                    {
                        mesh.Value.Remove(peerId);
                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                             .Ensure(r => r.Control.Prune)
                             .Add(new ControlPrune { TopicID = mesh.Key, Peers = { }, Backoff = 60 });
                    }
                }
            }

            foreach (string? fanoutTopic in fanout.Keys.ToArray())
            {
                if (fanoutLastPublished.GetOrAdd(fanoutTopic, _ => DateTime.Now).AddMilliseconds(settings.FanoutTtl) < DateTime.Now)
                {
                    fanout.Remove(fanoutTopic, out _);
                    fanoutLastPublished.Remove(fanoutTopic, out _);
                }
                else
                {
                    int peerCountToAdd = settings.Degree - fanout[fanoutTopic].Count;
                    if (peerCountToAdd > 0)
                    {
                        foreach (PeerId? peerId in gPeers[fanoutTopic].Where(p => !fanout[fanoutTopic].Contains(p)).Take(peerCountToAdd))
                        {
                            fanout[fanoutTopic].Add(peerId);
                        }
                    }
                }
            }

            IEnumerable<IGrouping<string, Message>> msgs = messageCache.ToList().GroupBy(m => m.Topic);

            foreach (string? topic in mesh.Keys.Concat(fanout.Keys).Distinct().ToArray())
            {
                IGrouping<string, Message>? msgsInTopic = msgs.FirstOrDefault(mit => mit.Key == topic);
                if (msgsInTopic is not null)
                {
                    ControlIHave ihave = new() { TopicID = topic };
                    ihave.MessageIDs.AddRange(msgsInTopic.Select(m => ByteString.CopyFrom(m.GetId().Bytes)));

                    foreach (PeerId? peer in gPeers[topic].Where(p => !mesh[topic].Contains(p) && !fanout[topic].Contains(p)).Take(settings.LazyDegree))
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

    public void Publish(string topicId, byte[] message)
    {
        ulong seqNo = this.seqNo++;
        byte[] seqNoBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        Rpc rpc = new Rpc().WithMessages(topicId, seqNo, LocalPeerId.Bytes, message, localPeer.Identity);

        foreach (PeerId peerId in fPeers[topicId])
        {
            peerState.GetValueOrDefault(peerId)?.Send(rpc);
        }
        if (mesh.ContainsKey(topicId))
        {
            foreach (PeerId peerId in mesh[topicId])
            {
                peerState.GetValueOrDefault(peerId)?.Send(rpc);
            }
        }
        else
        {
            fanoutLastPublished[topicId] = DateTime.Now;
            HashSet<PeerId> topicFanout = fanout.GetOrAdd(topicId, _ => new HashSet<PeerId>());

            if (topicFanout.Count == 0)
            {
                HashSet<PeerId>? topicPeers = gPeers.GetValueOrDefault(topicId);
                if (topicPeers is { Count: > 0 })
                {
                    foreach (PeerId peer in topicPeers.Take(settings.Degree))
                    {
                        topicFanout.Add(peer);
                    }
                }
            }

            foreach (PeerId peerId in topicFanout)
            {
                peerState.GetValueOrDefault(peerId)?.Send(rpc);
            }
        }
    }

    internal CancellationToken OutboundConnection(Multiaddress addr, string protocolId, Task dialTask, Action<Rpc> sendRpc)
    {
        PubsubPeer peer;
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null)
        {
            return Canceled;
        }

        peer = new PubsubPeer(peerId, protocolId) { Address = addr, SendRpc = sendRpc, InititatedBy = ConnectionInitiation.Local };

        if (!peerState.TryAdd(peerId, peer))
        {
            if (peerState[peerId].SendRpc is null)
            {
                peer.SendRpc = sendRpc;
            }
            else
            {
                return Canceled;
            }
        }

        dialTask.ContinueWith(t =>
        {
            peerState.GetValueOrDefault(peerId)?.TokenSource.Cancel();
            peerState.TryRemove(peerId, out _);
            foreach (var topicPeers in fPeers)
            {
                topicPeers.Value.Remove(peerId);
            }
            foreach (var topicPeers in gPeers)
            {
                topicPeers.Value.Remove(peerId);
            }
            foreach (var topicPeers in fanout)
            {
                topicPeers.Value.Remove(peerId);
            }
            foreach (var topicPeers in mesh)
            {
                topicPeers.Value.Remove(peerId);
            }
            reconnections.Add(new Reconnection([addr], settings.ReconnectionAttempts));
        });
        Rpc helloMessage = new Rpc().WithTopics(topicState.Keys.ToList(), Enumerable.Empty<string>());
        peer.Send(helloMessage);
        logger?.LogDebug("Outbound {peerId}", peerId);
        return peer.TokenSource.Token;
    }

    internal CancellationToken InboundConnection(Multiaddress addr, string protocolId, Task listTask, Task dialTask, Func<Task> subDial)
    {
        PubsubPeer? remotePeer;
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null)
        {
            return Canceled;
        }

        remotePeer = new PubsubPeer(peerId, protocolId) { Address = addr, InititatedBy = ConnectionInitiation.Remote };
        logger?.LogDebug("Inbound {peerId}", peerId);
        if (peerState.TryAdd(peerId, remotePeer))
        {
            logger?.LogDebug("Inbound, lets dial {peerId} via remotely initiated connection", peerId);
            listTask.ContinueWith(t =>
            {
                peerState.GetValueOrDefault(peerId)?.TokenSource.Cancel();
                peerState.TryRemove(peerId, out _);
                foreach (var topicPeers in fPeers)
                {
                    topicPeers.Value.Remove(peerId);
                }
                foreach (var topicPeers in gPeers)
                {
                    topicPeers.Value.Remove(peerId);
                }
                foreach (var topicPeers in fanout)
                {
                    topicPeers.Value.Remove(peerId);
                }
                foreach (var topicPeers in mesh)
                {
                    topicPeers.Value.Remove(peerId);
                }
                reconnections.Add(new Reconnection([addr], settings.ReconnectionAttempts));
            });

            subDial();
            return remotePeer.TokenSource.Token;
        }
        else
        {
            return peerState[peerId].TokenSource.Token;
        }
    }

    internal async Task OnRpc(PeerId peerId, Rpc rpc)
    {
        try
        {
            ConcurrentDictionary<PeerId, Rpc> peerMessages = new();
            lock (this)
            {

                if (rpc.Subscriptions.Any())
                {
                    foreach (Rpc.Types.SubOpts? sub in rpc.Subscriptions)
                    {
                        var state = peerState.GetValueOrDefault(peerId);
                        if (state is null)
                        {
                            return;
                        }
                        if (sub.Subscribe)
                        {
                            if (state.IsGossipSub)
                            {
                                gPeers.GetOrAdd(sub.Topicid, _ => new HashSet<PeerId>()).Add(peerId);
                            }
                            else if (state.IsFloodSub)
                            {
                                fPeers.GetOrAdd(sub.Topicid, _ => new HashSet<PeerId>()).Add(peerId);
                            }
                        }
                        else
                        {
                            if (state.IsGossipSub)
                            {
                                gPeers.GetOrAdd(sub.Topicid, _ => new HashSet<PeerId>()).Remove(peerId);
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
                                fPeers.GetOrAdd(sub.Topicid, _ => new HashSet<PeerId>()).Remove(peerId);
                            }
                        }
                    }
                }

                if (rpc.Publish.Any())
                {
                    if (rpc.Publish.Any())
                    {
                        logger?.LogDebug($"Messages received: {rpc.Publish.Select(message => message.GetId()).Count(messageId => limboMessageCache.Contains(messageId) || messageCache!.Contains(messageId))}/{rpc.Publish.Count}: {rpc.Publish.Count}");
                    }

                    foreach (Message? message in rpc.Publish)
                    {
                        MessageId messageId = message.GetId();

                        if (limboMessageCache.Contains(messageId) || messageCache!.Contains(messageId))
                        {
                            continue;
                        }

                        switch (VerifyMessage?.Invoke(message))
                        {
                            case MessageValidity.Rejected:
                            case MessageValidity.Ignored:
                                limboMessageCache.Add(messageId, message);
                                continue;
                            case MessageValidity.Trottled:
                                continue;
                        }

                        if (!message.VerifySignature(settings.DefaultSignaturePolicy))
                        {
                            limboMessageCache!.Add(messageId, message);
                            continue;
                        }

                        messageCache.Add(messageId, message);

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
                        if (fPeers.TryGetValue(message.Topic, out topicPeers))
                        {
                            foreach (PeerId peer in mesh[message.Topic])
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
                                mesh[graft.TopicID].Add(peerId);
                                peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                    .Ensure(r => r.Control.Graft)
                                    .Add(new ControlGraft { TopicID = graft.TopicID });
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
                            }
                        }
                    }

                    if (rpc.Control.Ihave.Any())
                    {
                        List<MessageId> messageIds = new();

                        foreach (ControlIHave? ihave in rpc.Control.Ihave
                            .Where(iw => topicState.ContainsKey(iw.TopicID)))
                        {
                            messageIds.AddRange(ihave.MessageIDs.Select(m => new MessageId(m.ToByteArray()))
                                .Where(mid => !messageCache.Contains(mid)));
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
                        List<Message> messages = new();
                        foreach (MessageId? mId in messageIds)
                        {
                            Message message = messageCache.Get(mId);
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
                        foreach (MessageId messageId in rpc.Control.Iwant.SelectMany(iw => iw.MessageIDs).Select(m => new MessageId(m.ToByteArray())).Take(settings.MaxIdontwantMessages))
                        {
                            dontWantMessages.Add((peerId, messageId));
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
            logger?.LogError("Exception during rpc handling: {exception}", ex);
        }
    }
}

internal enum ConnectionInitiation
{
    Local,
    Remote,
}
