// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Org.BouncyCastle.Tls;
using System.Collections.Concurrent;
using System.Diagnostics;

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

public partial class PubsubRouter(PeerStore store, ILoggerFactory? loggerFactory = default) : IRoutingStateContainer
{
    static int routerCounter = 0;
    readonly int routerId = Interlocked.Increment(ref routerCounter);

    public override string ToString()
    {
        //{string.Join("|", peerState.Select(x => $"{x.Key}:{x.Value.SendRpc is not null}"))}
        return $"Router#{routerId}: {localPeer?.Address.GetPeerId() ?? "null"}, " +
            $"peers: {peerState.Count(x => x.Value.SendRpc is not null)}/{peerState.Count}, " +
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
        public PubsubPeer(PeerId peerId, string protocolId)
        {
            PeerId = peerId;
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
        public Action<Rpc>? SendRpc
        {
            get => _sendRpc; set
            {
                Debug.WriteLine($"Set SENDRPC for {this.PeerId}: {value}");
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
    ICollection<PeerId> IRoutingStateContainer.ConnectedPeers => peerState.Keys;
    Task IRoutingStateContainer.Heartbeat() => Heartbeat();
    #endregion

    public event Action<string, byte[]>? OnMessage;
    public Func<Message, MessageValidity>? VerifyMessage = null;

    private Settings settings;
    private TtlCache<MessageId, Message> messageCache;
    private TtlCache<MessageId, Message> limboMessageCache;
    private TtlCache<(PeerId, MessageId)> dontWantMessages;

    private ILocalPeer? localPeer;
    private ManagedPeer peer;
    private readonly ILogger? logger = loggerFactory?.CreateLogger<PubsubRouter>();

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

    private readonly ConcurrentBag<Reconnection> reconnections = new();
    private ulong seqNo = 1;

    private record Reconnection(Multiaddress[] Addresses, int Attempts);

    static PubsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public async Task RunAsync(ILocalPeer localPeer, Settings? settings = null, CancellationToken token = default)
    {
        logger?.LogDebug($"Running pubsub for {localPeer.Address}");

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

        logger?.LogInformation("Started");

        store.OnNewPeer += (addrs) =>
        {
            if (addrs.Any(a => a.GetPeerId()! == localPeer.Identity.PeerId))
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    IRemotePeer remotePeer = await peer.DialAsync(addrs, token);

                    if (!peerState.ContainsKey(remotePeer.Address.Get<P2P>().ToString()))
                    {
                        await remotePeer.DialAsync<GossipsubProtocol>(token);
                        if (peerState.TryGetValue(remotePeer.Address.GetPeerId()!, out PubsubPeer? state) && state.InititatedBy == ConnectionInitiation.Remote)
                        {
                            _ = remotePeer.DisconnectAsync();
                        }
                    }
                }
                catch
                {
                    reconnections.Add(new Reconnection(addrs, this.settings.ReconnectionAttempts));
                }
            });
        };

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(this.settings.HeartbeatInterval);
                await Heartbeat();
            }
        }, token);

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


    private async Task Reconnect(CancellationToken token)
    {
        while (reconnections.TryTake(out Reconnection? rec))
        {
            try
            {
                IRemotePeer remotePeer = await peer.DialAsync(rec.Addresses, token);
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
                Console.WriteLine($"MESH({localPeer!.Address.GetPeerId()}) {mesh.Key}: {mesh.Value.Count} ({mesh.Value})");
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
                        var prune = new ControlPrune { TopicID = mesh.Key, Backoff = 60 };
                        prune.Peers.AddRange(mesh.Value.ToArray().Select(pid => (PeerId: pid, Record: store.GetPeerInfo(pid)?.SignedPeerRecord)).Where(pid => pid.Record is not null).Select(pid => new PeerInfo
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

            foreach (string? topic in gPeers.Keys.Concat(fanout.Keys).Distinct().ToArray())
            {
                IGrouping<string, Message>? msgsInTopic = msgs.FirstOrDefault(mit => mit.Key == topic);
                if (msgsInTopic is not null)
                {
                    ControlIHave ihave = new() { TopicID = topic };
                    ihave.MessageIDs.AddRange(msgsInTopic.Select(m => ByteString.CopyFrom(settings.GetMessageId(m).Bytes)));

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

    internal CancellationToken OutboundConnection(Multiaddress addr, string protocolId, Task dialTask, Action<Rpc> sendRpc)
    {
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null)
        {
            return Canceled;
        }

        PubsubPeer peer = peerState.GetOrAdd(peerId, (id) => new PubsubPeer(peerId, protocolId) { Address = addr, SendRpc = sendRpc, InititatedBy = ConnectionInitiation.Local });

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
                    return Canceled;
                }
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

    internal CancellationToken InboundConnection(Multiaddress addr, string protocolId, Task listTask, Task dialTask, Func<Task> subDial)
    {
        PeerId? peerId = addr.GetPeerId();

        if (peerId is null || peerId == localPeer!.Identity.PeerId)
        {
            return Canceled;
        }

        PubsubPeer? newPeer = null;
        PubsubPeer existingPeer = peerState.GetOrAdd(peerId, (id) => newPeer = new PubsubPeer(peerId, protocolId) { Address = addr, InititatedBy = ConnectionInitiation.Remote });
        if (newPeer is not null)
        {
            logger?.LogDebug("Inbound, let's dial {peerId} via remotely initiated connection", peerId);
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

            return newPeer.TokenSource.Token;
        }
        else
        {
            return existingPeer.TokenSource.Token;
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

                if (rpc.Publish.Any())
                {
                    if (rpc.Publish.Any())
                    {
                        logger?.LogDebug($"Messages received: {rpc.Publish.Select(settings.GetMessageId).Count(messageId => limboMessageCache.Contains(messageId) || messageCache!.Contains(messageId))}/{rpc.Publish.Count}: {rpc.Publish.Count}");
                    }

                    foreach (Message? message in rpc.Publish)
                    {
                        MessageId messageId = settings.GetMessageId(message);

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
                                HashSet<PeerId> topicMesh = mesh[graft.TopicID];

                                if (topicMesh.Count >= settings.HighestDegree)
                                {
                                    ControlPrune prune = new() { TopicID = graft.TopicID };

                                    if (peerState.TryGetValue(peerId, out PubsubPeer? state) && state.IsGossipSub && state.Protocol >= PubsubPeer.PubsubProtocol.GossipsubV11)
                                    {
                                        state.Backoff[prune.TopicID] = DateTime.Now.AddSeconds(prune.Backoff == 0 ? 60 : prune.Backoff);
                                        prune.Peers.AddRange(topicMesh.ToArray().Select(pid => (PeerId: pid, Record: store.GetPeerInfo(pid)?.SignedPeerRecord)).Where(pid => pid.Record is not null).Select(pid => new PeerInfo
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
                                    topicMesh.Add(peerId);
                                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                                        .Ensure(r => r.Control.Graft)
                                        .Add(new ControlGraft { TopicID = graft.TopicID });
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

                                foreach (var peer in prune.Peers)
                                {
                                    // TODO verify payload type, signature, etc
                                    // TODO check if it's working
                                    reconnections.Add(new Reconnection(PeerRecord.Parser.ParseFrom(SignedEnvelope.Parser.ParseFrom(peer.SignedPeerRecord).Payload).Addresses.Select(ai => Multiaddress.Decode(ai.Multiaddr.ToByteArray())).ToArray(), 5));
                                }
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
