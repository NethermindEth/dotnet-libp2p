// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.Gossipsub;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Enums;
using Nethermind.Libp2p.Protocols.Gossipsub;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;
using Org.BouncyCastle.Utilities.Encoders;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks.Dataflow;

namespace Nethermind.Libp2p.Protocols.Floodsub;

public class PubsubRouter
{
    public const string FloodsubProtocolVersion = "/floodsub/1.0.0";
    public const string GossipsubProtocolVersionV10 = "/meshsub/1.0.0";
    public const string GossipsubProtocolVersionV11 = "/meshsub/1.1.0";
    public const string GossipsubProtocolVersionV12 = "/meshsub/1.2.0";

    class PubsubPeer
    {
        public Action<Rpc> SendRpc { get; internal set; }
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId RawPeerId { get; init; }

        // replace with enum?
        public string Protocol { get; internal set; }
        public bool IsGossipSub => Protocol.StartsWith("/meshsub/");
        public bool IsFloodSub => Protocol.StartsWith("/floodsub/");
    }

    private static readonly CancellationToken Canceled;

    public PeerId? LocalPeerId { get; private set; }

    public event Action<string, byte[]>? OnMessage;

    private Settings settings;
    private TtlCache<MessageId, Message> messageCache;
    private ILocalPeer? localPeer;
    private ILogger? logger;
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> fPeers = new();
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> gPeers = new();
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> mesh = new();
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> fanout = new();
    private readonly ConcurrentDictionary<string, DateTime> fanoutLastPublished = new();

    private readonly ConcurrentDictionary<PeerId, PubsubPeer> peerState = new();
    private readonly ConcurrentDictionary<string, Topic> topicState = new();
    private ulong seqNo = 1;

    static PubsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public PubsubRouter(ILoggerFactory? loggerFactory = null)
    {
        logger = loggerFactory?.CreateLogger<PubsubRouter>();
    }

    public async Task RunAsync(ILocalPeer localPeer, IDiscoveryProtocol discoveryProtocol, Settings? settings = null, CancellationToken token = default)
    {
        this.settings = settings ?? Settings.Default;
        messageCache = new(this.settings.MessageCacheTtl);
        if (this.localPeer is not null)
        {
            throw new InvalidOperationException("Router has been already started");
        }
        this.localPeer = localPeer;
        LocalPeerId = new PeerId(localPeer.Address.At(Core.Enums.Multiaddr.P2p)!);

        _ = localPeer.ListenAsync(localPeer.Address, token);
        _ = StartDiscoveryAsync(discoveryProtocol, token);
        logger?.LogInformation("Started");

        await Task.Delay(Timeout.Infinite, token);
        messageCache.Dispose();
    }

    private async Task StartDiscoveryAsync(IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        if (localPeer is null)
        {
            throw new ArgumentNullException(nameof(localPeer));
        }
        ObservableCollection<Core.Multiaddr> col = new();
        discoveryProtocol.OnAddPeer = (addrs) =>
        {
            Dictionary<Core.Multiaddr, CancellationTokenSource> cancellations = new();
            foreach (Core.Multiaddr addr in addrs)
            {
                cancellations[addr] = CancellationTokenSource.CreateLinkedTokenSource(token);
            }

            _ = Task.Run(async () =>
            {
                IRemotePeer firstConnected = (await Task.WhenAny(addrs
                    .Select(addr => localPeer.DialAsync(addr)))).Result;
                foreach (KeyValuePair<Core.Multiaddr, CancellationTokenSource> c in cancellations)
                {
                    if (c.Key != firstConnected.Address)
                    {
                        c.Value.Cancel(false);
                    }
                }
                logger?.LogDebug("Dialing {0}", firstConnected.Address);
                PeerId peerId = firstConnected.Address.At(Core.Enums.Multiaddr.P2p)!;
                if (!peerState.ContainsKey(peerId))
                {
                    await firstConnected.DialAsync<FloodsubProtocol>(token);
                }
            });
            return true;
        };

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                _ = HeartBeat();
                await Task.Delay(settings.HeartbeatInterval);
            }
        }, token);

        await discoveryProtocol.DiscoverAsync(localPeer.Address, token);
    }

    public ITopic Subscribe(string topicId)
    {
        if (topicState.TryGetValue(topicId, out Topic? existingTopic))
        {
            return existingTopic;
        }

        Topic topic = topicState[topicId] = new(this, topicId);

        fPeers.TryAdd(topicId, new HashSet<PeerId>());
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

        Rpc topicUpdate = new Rpc().WithTopics(new string[] { topicId }, Enumerable.Empty<string>());
        Rpc topicUpdateAndGraft = topicUpdate.Clone();
        topicUpdateAndGraft.Control.Graft.Add(new ControlGraft { TopicID = topicId });

        foreach (string gPeer in gPeers.Keys)
        {
            if (peersToGraft.Contains(gPeer))
            {
                peerState[gPeer].SendRpc?.Invoke(topicUpdateAndGraft!);
            }
            else
            {
                peerState[gPeer].SendRpc?.Invoke(topicUpdate);
            }
        }

        topic.GraftingPeers = peersToGraft;

        return topic;
    }

    public void Unsubscribe(string topicId)
    {
        foreach (PeerId peerId in fPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics(Enumerable.Empty<string>(), new[] { topicId });

            peerState[peerId].SendRpc?.Invoke(msg);
        }
        foreach (PeerId peerId in gPeers[topicId])
        {
            Rpc msg = new Rpc()
                .WithTopics(Enumerable.Empty<string>(), new[] { topicId });

            if (mesh.TryGetValue(topicId, out HashSet<PeerId>? topicMesh) && topicMesh.Contains(peerId))
            {
                msg.Control.Prune.Add(new ControlPrune { TopicID = topicId });
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
                    .Control.Prune.Add(new ControlPrune { TopicID = topicMesh.Key });
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
        }
    }

    public async Task HeartBeat()
    {
        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();

        foreach (KeyValuePair<string, HashSet<PeerId>> mesh in mesh)
        {
            if (mesh.Value.Count < settings.LowestDegree)
            {
                string[] peersToGraft = gPeers.Keys.Where(p => !mesh.Value.Contains(p)).Take(settings.LowestDegree - mesh.Value.Count).ToArray();
                foreach (string? peerId in peersToGraft)
                {
                    mesh.Value.Add(peerId);
                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                        .Control.Graft.Add(new ControlGraft { TopicID = mesh.Key });
                }
            }
            else if (mesh.Value.Count > settings.HighestDegree)
            {
                PeerId[] peerstoPrune = mesh.Value.Take(mesh.Value.Count - settings.HighestDegree).ToArray();
                foreach (PeerId? peerId in peerstoPrune)
                {
                    mesh.Value.Remove(peerId);
                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                        .Control.Prune.Add(new ControlPrune { TopicID = mesh.Key });
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
                ControlIHave ihave = new ControlIHave { TopicID = topic };
                ihave.MessageIDs.AddRange(msgsInTopic.Select(m => ByteString.CopyFrom(m.GetId().Bytes)));

                foreach (PeerId? peer in gPeers[topic].Where(p => !mesh[topic].Contains(p) && !fanout[topic].Contains(p)).Take(settings.LazyDegree))
                {
                    peerMessages.GetOrAdd(peer, _ => new Rpc())
                       .Control.Ihave.Add(ihave);
                }
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
        }
    }

    public void Publish(string topicId, byte[] message)
    {
        ulong seqNo = this.seqNo++;
        byte[] seqNoBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        Rpc rpc = new Rpc().WithMessages(topicId, seqNo, LocalPeerId.Bytes, message, localPeer.Identity.PrivateKey);

        foreach (PeerId peer in fPeers[topicId])
        {
            peerState[peer].SendRpc?.Invoke(rpc);
        }
        if (mesh.ContainsKey(topicId))
        {
            foreach (PeerId peer in mesh[topicId])
            {
                peerState[peer].SendRpc?.Invoke(rpc);
            }
        }
    }

    internal CancellationToken OutboundConnection(PeerId peerId, string protocolId, Action<Rpc> sendRpc)
    {
        PubsubPeer peer;
        if (peerState.ContainsKey(peerId))
        {
            peer = peerState[peerId];
            if (peer.SendRpc is null)
            {
                peer.SendRpc = sendRpc;
            }
            else
            {
                return Canceled;
            }
        }
        else
        {
            peer = new PubsubPeer { RawPeerId = peerId, Protocol = protocolId, SendRpc = sendRpc, TokenSource = new CancellationTokenSource() };
            peerState.TryAdd(peerId, peer);
        }

        Rpc helloMessage = new Rpc().WithTopics(fPeers.Keys, Enumerable.Empty<string>());
        peer.SendRpc.Invoke(helloMessage);
        logger?.LogDebug("Outbound {0}", peerId);
        return peer.TokenSource.Token;
    }

    internal CancellationToken InboundConnection(PeerId peerId, string protocolId, Action subDial)
    {
        PubsubPeer? remotePeer;
        if (peerState.TryGetValue(peerId, out remotePeer) && remotePeer is not null)
        {
            return remotePeer.TokenSource.Token;
        }

        remotePeer = new PubsubPeer { RawPeerId = peerId, Protocol = protocolId, TokenSource = new CancellationTokenSource() };
        logger?.LogDebug("Inbound {0}", peerId);
        if (peerState.TryAdd(peerId, remotePeer))
        {
            logger?.LogDebug("Inbound, lets dial {0}", peerId);
            subDial();
            return remotePeer.TokenSource.Token;
        }
        else
        {
            return peerState[peerId].TokenSource.Token;
        }
    }

    internal void OnRpc(PeerId peerId, Rpc rpc)
    {
        ConcurrentDictionary<PeerId, Rpc> peerMessages = new();

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
            foreach (Message? message in rpc.Publish)
            {
                MessageId messageId = message.GetId();
                if (messageCache!.Contains(messageId))
                {
                    continue;
                }
                messageCache.Add(messageId, message);
                PeerId author = new(message.From.ToArray());
                if (!message.VerifySignature())
                {
                    continue;
                }
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
                            .Control.Prune.Add(new ControlPrune { TopicID = graft.TopicID });
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
                        mesh[prune.TopicID].Remove(peerId);
                        peerMessages.GetOrAdd(peerId, _ => new Rpc())
                            .Control.Prune.Add(new ControlPrune { TopicID = prune.TopicID });
                    }
                }
            }

            if (rpc.Control.Ihave.Any())
            {
                List<MessageId> messageIds = new List<MessageId>();

                foreach (ControlIHave? ihave in rpc.Control.Ihave
                    .Where(iw => topicState.ContainsKey(iw.TopicID)))
                {
                    messageIds.AddRange(ihave.MessageIDs.Select(m => new MessageId(m.ToByteArray()))
                        .Where(mid => !messageCache.Contains(mid)));
                }
                if (messageIds.Any())
                {
                    ControlIWant ciw = new ControlIWant();
                    foreach (MessageId mId in messageIds)
                    {
                        ciw.MessageIDs.Add(ByteString.CopyFrom(mId.Bytes));
                    }
                    peerMessages.GetOrAdd(peerId, _ => new Rpc())
                        .Control.Iwant.Add(ciw);
                }
            }

            if (rpc.Control.Iwant.Any())
            {
                IEnumerable<MessageId> messageIds = rpc.Control.Iwant.SelectMany(iw => iw.MessageIDs).Select(m => new MessageId(m.ToByteArray()));
                List<Message> messages = new List<Message>();
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

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            peerState[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
        }
    }
}
