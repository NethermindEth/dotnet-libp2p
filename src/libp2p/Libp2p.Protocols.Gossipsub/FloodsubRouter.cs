// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Enums;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;
using Org.BouncyCastle.Utilities.Encoders;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.Caching;
using System.Text;

namespace Libp2p.Protocols.Floodsub;
public class FloodsubRouter
{
    private ILocalPeer localPeer;
    private ILogger? logger;

    ConcurrentDictionary<string, HashSet<PeerId>> Topics = new();
    ConcurrentDictionary<PeerId, Peer> Peers = new();
    ulong SeqNo = 1;

    public PeerId LocalPeerId { get; private set; }

    public event Action<string, byte[]> OnMessage;

    public ObjectCache MessageCache = new MemoryCache("{nameof(FloodsubRouter)}MessageCache");
    private static CancellationToken Canceled;

    static FloodsubRouter()
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    class Peer
    {
        public Action<Rpc> SendRpc { get; internal set; }
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId PeerId { get; init; }
    }

    public FloodsubRouter(ILoggerFactory? loggerFactory = null)
    {
        logger = loggerFactory?.CreateLogger<FloodsubRouter>();
    }

    public async Task StartAsync(ILocalPeer localPeer, IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        if (this.localPeer is not null)
        {
            throw new InvalidOperationException("Router has been already started");
        }
        this.localPeer = localPeer;
        LocalPeerId = new PeerId(localPeer.Address.At(Multiaddr.P2p)!);

        IListener listener = await localPeer.ListenAsync(localPeer.Address, token);
        _ = StartDiscoveryAsync(discoveryProtocol);

        logger?.LogInformation("Started");
    }

    private async Task StartDiscoveryAsync(IDiscoveryProtocol discoveryProtocol)
    {
        ObservableCollection<MultiAddr> col = new();
        discoveryProtocol.OnAddPeer = (addrs) =>
        {
            Dictionary<MultiAddr, CancellationTokenSource> cancellations = new();
            foreach (MultiAddr addr in addrs)
            {
                cancellations[addr] = new CancellationTokenSource();
            }

            _ = Task.Run(async () =>
            {
                IRemotePeer firstConnected = (await Task.WhenAny(addrs
                    .Where(x => !x.ToString().Contains("192.168"))
                    .Select(addr => localPeer.DialAsync(addr)))).Result;
                foreach (KeyValuePair<MultiAddr, CancellationTokenSource> c in cancellations)
                {
                    if (c.Key != firstConnected.Address)
                    {
                        //logger?.LogDebug("Cancel {0}", c.Key);
                        c.Value.Cancel(false);
                    }
                }
                logger?.LogDebug("Dialing {0}", firstConnected.Address);
                PeerId peerId = firstConnected.Address.At(Multiaddr.P2p);
                if (!Peers.ContainsKey(peerId))
                {
                    await firstConnected.DialAsync<FloodsubProtocol>();
                }
            });

            return true;
        };

        await discoveryProtocol.DiscoverAsync(localPeer.Address);
    }

    public ITopic Subscribe(string topicName)
    {
        Topic topic = new(this, topicName);
        Topics.TryAdd(topicName, new HashSet<PeerId>());
        HashSet<PeerId> peers = Topics[topicName];
        Rpc topicUpdate = new Rpc().WithTopics(new string[] { topicName }, Enumerable.Empty<string>());
        foreach (PeerId peer in peers)
        {
            Peers[peer].SendRpc?.Invoke(topicUpdate);
        }
        return topic;
    }

    public void Unsubscribe(string topicName)
    {
        if (Topics.ContainsKey(topicName))
        {
            if (!Topics[topicName].Any())
            {
                Topics.Remove(topicName, out _);
            }

            Rpc topicUpdate = new Rpc().WithTopics(Topics.Keys.Where(tn => tn != topicName), new[] { topicName });
            foreach (KeyValuePair<PeerId, Peer> peer in Peers!)
            {
                peer.Value.SendRpc?.Invoke(topicUpdate);
            }
        }
    }

    public void Publish(string topic, byte[] message)
    {
        ulong seqNo = SeqNo++;
        byte[] seqNoBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        Rpc rpc = new Rpc().WithMessages(topic, seqNo, LocalPeerId.Bytes, message, localPeer.Identity.PrivateKey);
        string messageId = Encoding.ASCII.GetString(Base64.Encode(LocalPeerId.Bytes.Concat(seqNoBytes).ToArray()));
        MessageCache.Set(messageId, message, DateTimeOffset.Now.AddMinutes(5));

        foreach (PeerId peer in Topics[topic])
        {
            Peers[peer].SendRpc?.Invoke(rpc);
        }
    }

    internal CancellationToken OutboundConnection(PeerId peerId, Action<Rpc> sendRpc)
    {
        Peer peer;
        if (Peers.ContainsKey(peerId))
        {
            peer = Peers[peerId];
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
            peer = new Peer { PeerId = peerId, SendRpc = sendRpc, TokenSource = new CancellationTokenSource() };
            Peers.TryAdd(peerId, peer);
        }
        peer.SendRpc.Invoke(new Rpc().WithTopics(Topics.Keys, Enumerable.Empty<string>()));
        logger?.LogDebug("Outbound {0}", peerId);
        return peer.TokenSource.Token;
    }

    internal CancellationToken InboundConnection(PeerId peerId, Action subDial)
    {
        Peer? remotePeer;
        if (Peers.TryGetValue(peerId, out remotePeer) && remotePeer is not null)
        {
            return remotePeer.TokenSource.Token;
        }

        remotePeer = new Peer { PeerId = peerId, TokenSource = new CancellationTokenSource() };
        logger?.LogDebug("Inbound {0}", peerId);
        if (Peers.TryAdd(peerId, remotePeer))
        {
            logger?.LogDebug("Inbound, lets dial {0}", peerId);
            subDial();
            return remotePeer.TokenSource.Token;
        }
        else
        {
            return Peers[peerId].TokenSource.Token;
        }
    }

    internal void OnRpc(PeerId peerId, Rpc rpc)
    {
        Dictionary<PeerId, Rpc> peerMessages = new Dictionary<PeerId, Rpc>();

        if (rpc.Subscriptions.Any())
        {
            foreach (Rpc.Types.SubOpts? sub in rpc.Subscriptions)
            {
                if (sub.Subscribe)
                {
                    if (!Topics.ContainsKey(sub.Topicid))
                    {
                        Topics[sub.Topicid] = new HashSet<PeerId>();
                    }
                    Topics[sub.Topicid].Add(peerId);
                }
                else
                {
                    if (Topics.ContainsKey(sub.Topicid))
                    {
                        Topics[sub.Topicid].Remove(peerId);
                    }
                }
            }

            string[] topicsToSend = rpc.Subscriptions.Where(s => s.Subscribe).Select(s => s.Topicid).ToArray();
            IEnumerable<Message?> messages = MessageCache.Select(s => s.Value as Message).Where(m => topicsToSend.Contains(m?.Topic));
            if (peerMessages.ContainsKey(peerId))
                peerMessages[peerId].Publish.AddRange(messages);
        }

        if (rpc.Publish.Any())
        {
            foreach (Message? message in rpc.Publish)
            {
                string messageId = Convert.ToHexString(message.From.Concat(message.Seqno).ToArray());
                if (MessageCache.Contains(messageId))
                {
                    continue;
                }
                OnMessage?.Invoke(message.Topic, message.Data.ToByteArray());
                PeerId author = new PeerId(message.From.ToArray());
                MessageCache.Set(messageId, message, DateTimeOffset.Now.AddMinutes(5));
                foreach (PeerId peer in Topics[message.Topic])
                {
                    if (peer == author || peer == peerId)
                    {
                        continue;
                    }
                    if (!peerMessages.ContainsKey(peer))
                    {
                        peerMessages[peer] = new Rpc();
                    }
                    peerMessages[peer].Publish.Add(message);
                }
            }
        }

        foreach (KeyValuePair<PeerId, Rpc> peerMessage in peerMessages)
        {
            Peers[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
        }
    }
}

public interface ITopic
{
    event Action<byte[]>? OnMessage;
    void Publish(byte[] bytes);
}

class Topic : ITopic
{
    private readonly FloodsubRouter router;
    private string topicName;

    public event Action<byte[]>? OnMessage;

    public Topic(FloodsubRouter router, string topicName)
    {
        this.router = router;
        this.topicName = topicName;
        router.OnMessage += (topicName, message) =>
        {
            if (OnMessage is not null && this.topicName == topicName)
            {
                OnMessage(message);
            }
        };
    }

    public void Publish(byte[] value)
    {
        router.Publish(topicName, value);
    }
}
