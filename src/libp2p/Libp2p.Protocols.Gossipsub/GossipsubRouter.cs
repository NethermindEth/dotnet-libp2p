//// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
//// SPDX-License-Identifier: MIT

//using Microsoft.Extensions.Logging;
//using Nethermind.Libp2p.Core;
//using Nethermind.Libp2p.Core.Discovery;
//using Nethermind.Libp2p.Protocols;
//using Nethermind.Libp2p.Protocols.GossipSub.Dto;
//using System.Collections.ObjectModel;

//namespace Libp2p.Protocols.Gossipsub;
//public class GossipsubRouter
//{
//    private ILocalPeer peer;
//    private ILogger? logger;

//    public static GossipsubRouter Instance = default!;

//    internal Dictionary<string, (Topic topic, HashSet<string> peers)> Fanout = new();
//    internal Dictionary<string, (Topic topic, HashSet<string> peers)> Mesh = new();
//    internal Dictionary<ulong, (string topic, byte[] message)> Messages = new();

//    public GossipsubRouter(ILocalPeer peer, ILoggerFactory? loggerFactory = null)
//    {
//        Instance = this;
//        this.peer = peer;
//        logger = loggerFactory?.CreateLogger<GossipsubRouter>();
//    }

//    public async Task StartAsync(IDiscoveryProtocol discoveryProtocol, CancellationToken token = default)
//    {
//        IListener listener = await peer.ListenAsync(peer.Address, token);

//        _ = StartDiscoveryAsync(discoveryProtocol);



//        logger?.LogInformation("Started");
//    }

//    public void Handle(Rpc rpc)
//    {

//    }

//    public void Connect(string peerId, x)
//    {

//    }

//    private async Task StartDiscoveryAsync(IDiscoveryProtocol discoveryProtocol)
//    {
//        ObservableCollection<MultiAddr> col = new();
//        discoveryProtocol.OnAddPeer = (addr) =>
//        {
//            _ = Task.Run(async () =>
//            {
//                IRemotePeer dialer = await peer.DialAsync(addr);
//                await dialer.DialAsync<GossipsubProtocol>();
//            });

//            return true;
//        };

//        await discoveryProtocol.DiscoverAsync(peer.Address);
//    }

//    public ITopic Subscribe(string topicName)
//    {
//        Topic topic = new (topicName);
//        GossipsubProtocol.Topics.Add(topicName, new(topic, new HashSet<string>()));

//        return topic;
//    }
//}

//public interface ITopic
//{
//    void OnMessage(Action<byte[]> value);

//    void Publish(byte[] bytes);
//}

//delegate void OnMessage(byte[] message);

//class Topic : ITopic
//{
//    private string topicName;

//    public OnMessage? onMessage;

//    public Topic(string topicName)
//    {
//        this.topicName = topicName;
//    }

//    public void OnMessage(Action<byte[]> value)
//    {
//        this.onMessage += new OnMessage(value);
//    }

//    public void Publish(byte[] value)
//    {
//        GossipsubProtocol.SendIHave?.Invoke(topicName, value);
//    }
//}


//public class GossipsubRouterV11
//{

//}
