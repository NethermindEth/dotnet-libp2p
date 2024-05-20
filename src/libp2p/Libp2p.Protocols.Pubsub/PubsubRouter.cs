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

public class PubsubRouter : IRoutingStateContainer
{
    public const string FloodsubProtocolVersion = "/floodsub/1.0.0";
    public const string GossipsubProtocolVersionV10 = "/meshsub/1.0.0";
    public const string GossipsubProtocolVersionV11 = "/meshsub/1.1.0";
    //public const string GossipsubProtocolVersionV12 = "/meshsub/1.2.0";

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
        }

        private enum PubsubProtocol
        {
            Unknown,
            Floodsub,
            Gossipsub,
        }

        public Dictionary<string, DateTime> Backoff { get; internal set; }
        public Action<Rpc>? SendRpc { get; internal set; }
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId PeerId { get; set; }

        private PubsubProtocol Protocol { get; set; }
        public bool IsGossipSub => Protocol == PubsubProtocol.Gossipsub;
        public bool IsFloodSub => Protocol == PubsubProtocol.Floodsub;

        public ConnectionInitiation InititatedBy { get; internal set; }
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
    private ILocalPeer? localPeer;
    private ManagedPeer peer;
    private ILogger? logger;

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
    private ulong seqNo = 1;

    static PubsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public PubsubRouter(ILoggerFactory? loggerFactory = default)
    {
        logger = loggerFactory?.CreateLogger<PubsubRouter>();
    }

    public async Task RunAsync(ILocalPeer localPeer, IDiscoveryProtocol discoveryProtocol, Settings? settings = null, CancellationToken token = default)
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

        LocalPeerId = new PeerId(localPeer.Address.Get<P2P>().ToString()!);

        _ = localPeer.ListenAsync(localPeer.Address, token);
        _ = StartDiscoveryAsync(discoveryProtocol, token);
        logger?.LogInformation("Started");

        await Task.Delay(Timeout.Infinite, token);
        messageCache.Dispose();
        limboMessageCache.Dispose();
    }

    private async Task StartDiscoveryAsync(IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        if (localPeer is null)
        {
            throw new ArgumentNullException(nameof(localPeer));
        }

        ObservableCollection<Multiaddress> col = new();
        discoveryProtocol.OnAddPeer = (addrs) =>
        {
            _ = Task.Run(async () =>
            {
                IRemotePeer remotePeer = await peer.DialAsync(addrs, token);

                if (!peerState.ContainsKey(remotePeer.Identity.PeerId))
                {
                    await remotePeer.DialAsync<GossipsubProtocolV11>(token);
                    if (peerState.TryGetValue(remotePeer.Identity.PeerId, out PubsubPeer? state) && state.InititatedBy == ConnectionInitiation.Remote)
                    {
                        _ = remotePeer.DisconnectAsync();
                    }
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

    public ITopic Subscribe(string topicId)
    {
        Topic topic = topicState.GetOrAdd(topicId, (tId) => new(this, tId));

        if (!fPeers.TryAdd(topicId, new HashSet<PeerId>()))
        {
            // Already exists
            return topic;
        }

        gPeers.TryAdd(topicId, new HashSet<PeerId>());
        mesh[topicId] = new HashSet<PeerId>();
        HashSet<PeerId> peersToGraft = fanout.Remove(topicId, out HashSet<PeerId>? fanoutPeers) ?
            fanoutPeers :
            new HashSet<PeerId>();
        fanoutLastPublished.Remove(topicId, out _);

        foreach (PeerId peerId in gPeers[topicId])
        {
            if (peersToGraft.Count >= settings.Degree)
            {
                break;
            }

            peersToGraft.Add(peerId);
        }

        Rpc topicUpdate = new Rpc().WithTopics([topicId], Enumerable.Empty<string>());
        //Rpc topicUpdateAndGraft = topicUpdate.Clone();
        //topicUpdateAndGraft.Control = new();
        //topicUpdateAndGraft.Control.Graft.Add(new ControlGraft { TopicID = topicId });

        //foreach (PeerId gPeer in gPeers[topicId])
        //{
        //    if (peersToGraft.Contains(gPeer))
        //    {
        //        peerState[gPeer].SendRpc?.Invoke(topicUpdateAndGraft!);
        //    }
        //    else
        //    {
        //        peerState[gPeer].SendRpc?.Invoke(topicUpdate);
        //    }
        //}

        //topic.GraftingPeers = peersToGraft;

        return topic;
    }

    public void Unsubscribe(string topicId)
    {
        foreach (PeerId peerId in fPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics([], [topicId]);

            peerState[peerId].SendRpc?.Invoke(msg);
        }
        foreach (PeerId peerId in gPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics([], [topicId]);

            if (mesh.TryGetValue(topicId, out HashSet<PeerId>? topicMesh) && topicMesh.Contains(peerId))
            {
                msg.Ensure(r => r.Control.Prune).Add(new ControlPrune { TopicID = topicId });
            }
            peerState[peerId].SendRpc?.Invoke(msg);
        }
    }

    public void UnsubscribeAll()
    {
        foreach (PeerId? peerId in fPeers.SelectMany(kv => kv.Value))
        {
            Rpc msg = new Rpc()
                .WithTopics(Enumerable.Empty<string>(), topicState.Keys);

            peerState[peerId].SendRpc?.Invoke(msg);
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
            peerState[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
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
                    PeerId[] peersToGraft = gPeers[mesh.Key].Where(p => !mesh.Value.Contains(p) && peerState[p].SendRpc is not null && (!peerState[p].Backoff.TryGetValue(mesh.Key, out DateTime backoff) || backoff < DateTime.Now)).Take(settings.Degree - mesh.Value.Count).ToArray();
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
            peerState[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
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
            peerState[peerId].SendRpc?.Invoke(rpc);
        }
        if (mesh.ContainsKey(topicId))
        {
            foreach (PeerId peerId in mesh[topicId])
            {
                peerState[peerId].SendRpc?.Invoke(rpc);
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
                peerState[peerId].SendRpc?.Invoke(rpc);
            }
        }
    }

    internal CancellationToken OutboundConnection(PeerId peerId, string protocolId, Task dialTask, Action<Rpc> sendRpc)
    {
        PubsubPeer peer;
        peer = new PubsubPeer(peerId, protocolId) { SendRpc = sendRpc, InititatedBy = ConnectionInitiation.Local };

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

        Rpc helloMessage = new Rpc().WithTopics(topicState.Keys.ToList(), Enumerable.Empty<string>());
        peer.SendRpc.Invoke(helloMessage);
        logger?.LogDebug("Outbound {peerId}", peerId);
        return peer.TokenSource.Token;
    }

    internal CancellationToken InboundConnection(PeerId peerId, string protocolId, Task listTask, Task dialTask, Func<Task> subDial)
    {
        PubsubPeer? remotePeer;

        remotePeer = new PubsubPeer(peerId, protocolId) { InititatedBy = ConnectionInitiation.Remote };
        logger?.LogDebug("Inbound {peerId}", peerId);
        if (peerState.TryAdd(peerId, remotePeer))
        {
            logger?.LogDebug("Inbound, lets dial {peerId}", peerId);

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
            logger?.LogDebug("LOCKIN");
            lock (this)
            {

                if (rpc.Subscriptions.Any())
                {
                    foreach (Rpc.Types.SubOpts? sub in rpc.Subscriptions)
                    {
                        if (sub.Subscribe)
                        {
                            if (peerState[peerId].IsGossipSub)
                            {
                                gPeers.GetOrAdd(sub.Topicid, _ => new HashSet<PeerId>()).Add(peerId);
                            }
                            else if (peerState[peerId].IsFloodSub)
                            {
                                fPeers.GetOrAdd(sub.Topicid, _ => new HashSet<PeerId>()).Add(peerId);
                            }
                        }
                        else
                        {
                            if (peerState[peerId].IsGossipSub)
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
                            else if (peerState[peerId].IsFloodSub)
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
                            }
                        }
                    }

                    if (rpc.Control.Prune.Any())
                    {
                        foreach (ControlPrune? prune in rpc.Control.Prune)
                        {
                            if (topicState.ContainsKey(prune.TopicID) && mesh[prune.TopicID].Contains(peerId))
                            {
                                peerState[peerId].Backoff[prune.TopicID] = DateTime.Now.AddSeconds(prune.Backoff == 0 ? 60 : prune.Backoff);
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
                }
            }
            foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
            {
                peerState[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
            }
        }
        catch
        {

        }
        finally
        {
            logger?.LogDebug("LOCKOUT");
        }
    }
}

internal enum ConnectionInitiation
{
    Local,
    Remote,
}
