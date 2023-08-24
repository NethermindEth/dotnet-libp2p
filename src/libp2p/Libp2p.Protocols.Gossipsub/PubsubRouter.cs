// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Gossipsub;
using Microsoft.Extensions.Logging;
using Multiformats.Hash;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Enums;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;
using Org.BouncyCastle.Utilities.Encoders;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Multihash = Multiformats.Hash.Multihash;

namespace Nethermind.Libp2p.Protocols.Floodsub;

public class PubsubRouter
{
    class PubsubPeer
    {
        public Action<Rpc> SendRpc { get; internal set; }
        public CancellationTokenSource TokenSource { get; init; }
        public PeerId RawPeerId { get; init; }
    }

    private static readonly CancellationToken Canceled;

    public PeerId? LocalPeerId { get; private set; }

    public event Action<string, byte[]>? OnMessage;

    private TtlCache<string, Message>? messageCache;
    private ILocalPeer? localPeer;
    private ILogger? logger;
    private readonly ConcurrentDictionary<string, HashSet<PeerId>> topics = new();
    private readonly ConcurrentDictionary<PeerId, PubsubPeer> peers = new();
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

    public async Task RunAsync(ILocalPeer localPeer, IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
    {
        messageCache = new(30_000);
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
            foreach (KeyValuePair<PeerId, PubsubPeer> peer in peers!)
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

    internal CancellationToken OutboundConnection(PeerId peerId, string protocolId, Action<Rpc> sendRpc)
    {
        PubsubPeer peer;
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
            peer = new PubsubPeer { RawPeerId = peerId, SendRpc = sendRpc, TokenSource = new CancellationTokenSource() };
            peers.TryAdd(peerId, peer);
        }
        peer.SendRpc.Invoke(new Rpc().WithTopics(topics.Keys, Enumerable.Empty<string>()));
        logger?.LogDebug("Outbound {0}", peerId);
        return peer.TokenSource.Token;
    }

    internal CancellationToken InboundConnection(PeerId peerId, string protocolId, Action subDial)
    {
        PubsubPeer? remotePeer;
        if (peers.TryGetValue(peerId, out remotePeer) && remotePeer is not null)
        {
            return remotePeer.TokenSource.Token;
        }

        remotePeer = new PubsubPeer { RawPeerId = peerId, TokenSource = new CancellationTokenSource() };
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
                if (messageCache!.Contains(messageId))
                {
                    continue;
                }
                messageCache.Add(messageId, message);
                PeerId author = new(message.From.ToArray());
                if (!VerifySignature(author, message))
                {
                    continue;
                }
                OnMessage?.Invoke(message.Topic, message.Data.ToByteArray());
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

    private static bool VerifySignature(PeerId author, Message message)
    {
        Multihash multihash = Multihash.Decode(author.Bytes);
        if (multihash.Code != HashType.ID)
        {
            return false;
        }
        var pubKey = PublicKey.Parser.ParseFrom(multihash.Digest);
        if (pubKey.Type != KeyType.Ed25519)
        {
            return false;
        }
        return multihash.Code == HashType.ID && message.IsValid(pubKey.Data.ToByteArray());
    }
}
