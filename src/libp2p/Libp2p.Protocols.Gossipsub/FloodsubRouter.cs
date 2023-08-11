// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Gossipsub;
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
using System.Text;

namespace Libp2p.Protocols.Floodsub;
public partial class FloodsubRouter
{
    private static readonly CancellationToken Canceled;

    public PeerId? LocalPeerId { get; private set; }

    public event Action<string, byte[]>? OnMessage;

    private TtlCache<string, Message> messageCache;
    private ILocalPeer? localPeer;
    private ILogger? logger;
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> topics = new();
    private readonly ConcurrentDictionary<PeerId, PubSubPeer> peers = new();
    private ulong seqNo = 1;

    static FloodsubRouter()
    {
        CancellationTokenSource cts = new();
        cts.Cancel(false);
        Canceled = cts.Token;
    }

    public FloodsubRouter(ILoggerFactory? loggerFactory = null)
    {
        logger = loggerFactory?.CreateLogger<FloodsubRouter>();
    }

    public async Task RunAsync(ILocalPeer localPeer, IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        messageCache = new(30_000);
        if (this.localPeer is not null)
        {
            throw new InvalidOperationException("Router has been already started");
        }
        this.localPeer = localPeer;
        LocalPeerId = new PeerId(localPeer.Address.At(Multiaddr.P2p)!);

        _ = localPeer.ListenAsync(localPeer.Address, token);
        _ = StartDiscoveryAsync(discoveryProtocol, token);
        logger?.LogInformation("Started");

        await Task.Delay(Timeout.Infinite, token);
        messageCache.Dispose();
    }

    private async Task StartDiscoveryAsync(IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        ObservableCollection<MultiAddr> col = new();
        discoveryProtocol.OnAddPeer = (addrs) =>
        {
            Dictionary<MultiAddr, CancellationTokenSource> cancellations = new();
            foreach (MultiAddr addr in addrs)
            {
                cancellations[addr] = CancellationTokenSource.CreateLinkedTokenSource(token);
            }

            _ = Task.Run(async () =>
            {
                IRemotePeer firstConnected = (await Task.WhenAny(addrs
                    .Select(addr => localPeer.DialAsync(addr)))).Result;
                foreach (KeyValuePair<MultiAddr, CancellationTokenSource> c in cancellations)
                {
                    if (c.Key != firstConnected.Address)
                    {
                        c.Value.Cancel(false);
                    }
                }
                logger?.LogDebug("Dialing {0}", firstConnected.Address);
                PeerId peerId = firstConnected.Address.At(Multiaddr.P2p)!;
                if (!peers.ContainsKey(peerId))
                {
                    await firstConnected.DialAsync<FloodsubProtocol>(token);
                }
            });

            return true;
        };

        await discoveryProtocol.DiscoverAsync(localPeer.Address, token);
    }

    public ITopic Subscribe(string topicName)
    {
        Topic topic = new(this, topicName);
        topics.TryAdd(topicName, new HashSet<PeerId>());
        HashSet<PeerId> peers = topics[topicName];
        Rpc topicUpdate = new Rpc().WithTopics(new string[] { topicName }, Enumerable.Empty<string>());
        foreach (PeerId peer in peers)
        {
            this.peers[peer].SendRpc?.Invoke(topicUpdate);
        }
        return topic;
    }

    public void Unsubscribe(string topicName)
    {
        if (topics.ContainsKey(topicName))
        {
            if (!topics[topicName].Any())
            {
                topics.Remove(topicName, out _);
            }

            Rpc topicUpdate = new Rpc().WithTopics(topics.Keys.Where(tn => tn != topicName), new[] { topicName });
            foreach (KeyValuePair<PeerId, PubSubPeer> peer in peers!)
            {
                peer.Value.SendRpc?.Invoke(topicUpdate);
            }
        }
    }

    public void Publish(string topic, byte[] message)
    {
        ulong seqNo = this.seqNo++;
        byte[] seqNoBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        Rpc rpc = new Rpc().WithMessages(topic, seqNo, LocalPeerId.Bytes, message, localPeer.Identity.PrivateKey);
        string messageId = Encoding.ASCII.GetString(Base64.Encode(LocalPeerId.Bytes.Concat(seqNoBytes).ToArray()));

        foreach (PeerId peer in topics[topic])
        {
            peers[peer].SendRpc?.Invoke(rpc);
        }
    }

    internal CancellationToken OutboundConnection(PeerId peerId, Action<Rpc> sendRpc)
    {
        PubSubPeer peer;
        if (peers.ContainsKey(peerId))
        {
            peer = peers[peerId];
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
            peer = new PubSubPeer { RawPeerId = peerId, SendRpc = sendRpc, TokenSource = new CancellationTokenSource() };
            peers.TryAdd(peerId, peer);
        }
        peer.SendRpc.Invoke(new Rpc().WithTopics(topics.Keys, Enumerable.Empty<string>()));
        logger?.LogDebug("Outbound {0}", peerId);
        return peer.TokenSource.Token;
    }

    internal CancellationToken InboundConnection(PeerId peerId, Action subDial)
    {
        PubSubPeer? remotePeer;
        if (peers.TryGetValue(peerId, out remotePeer) && remotePeer is not null)
        {
            return remotePeer.TokenSource.Token;
        }

        remotePeer = new PubSubPeer { RawPeerId = peerId, TokenSource = new CancellationTokenSource() };
        logger?.LogDebug("Inbound {0}", peerId);
        if (peers.TryAdd(peerId, remotePeer))
        {
            logger?.LogDebug("Inbound, lets dial {0}", peerId);
            subDial();
            return remotePeer.TokenSource.Token;
        }
        else
        {
            return peers[peerId].TokenSource.Token;
        }
    }

    internal void OnRpc(PeerId peerId, Rpc rpc)
    {
        Dictionary<PeerId, Rpc> peerMessages = new();

        if (rpc.Subscriptions.Any())
        {
            foreach (Rpc.Types.SubOpts? sub in rpc.Subscriptions)
            {
                if (sub.Subscribe)
                {
                    if (!topics.ContainsKey(sub.Topicid))
                    {
                        topics[sub.Topicid] = new HashSet<PeerId>();
                    }
                    topics[sub.Topicid].Add(peerId);
                }
                else
                {
                    if (topics.ContainsKey(sub.Topicid))
                    {
                        topics[sub.Topicid].Remove(peerId);
                    }
                }
            }
        }

        if (rpc.Publish.Any())
        {
            foreach (Message? message in rpc.Publish)
            {
                string messageId = Convert.ToHexString(message.From.Concat(message.Seqno).ToArray());
                if (messageCache.Contains(messageId))
                {
                    continue;
                }
                OnMessage?.Invoke(message.Topic, message.Data.ToByteArray());
                PeerId author = new (message.From.ToArray());
                messageCache.Add(messageId, message);
                foreach (PeerId peer in topics[message.Topic])
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
            peers[peerMessage.Key].SendRpc?.Invoke(peerMessage.Value);
        }
    }
}
