// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using System.Threading.Channels;

public class ShutterP2P
{
    public static string ShutterP2PProtocolVersion = "/shutter/0.1.0";
    public static string ShutterP2PAgentVersion = "github.com/shutter-network/rolling-shutter/rolling-shutter";
    public static string ShutterP2PPort = "23102";
    private readonly Channel<byte[]> _msgQueue = Channel.CreateUnbounded<byte[]>();
    private PubsubRouter? _router;
    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _cancellationTokenSource;

    public static void Main()
    {
        IEnumerable<string> p2pAddresses = [
            // gnosis test
            // "/ip4/207.154.243.191/tcp/23000/p2p/12D3KooWPjX9v7FWmPvAUSTMpG7j2jXWxNnUxyDZrXPqmK29QNEd",
            // "/ip4/207.154.243.191/tcp/23001/p2p/12D3KooWJGopzaxs5G2FCPPMAy9zUoEdp5X7VebANvHGwiKW61Ck",
            // "/ip4/207.154.243.191/tcp/23002/p2p/12D3KooWPV5c2K5oHugFFArAwaJfxUrrTEjjwJgCQ44LNTsi7p68",
            // "/ip4/207.154.243.191/tcp/23003/p2p/12D3KooWKZBm2JvZE9SyPy3bQupgxtHzJC4y3E7xaHX5aRVewspC",
            // chiado
            "/ip4/164.92.188.205/tcp/23000/p2p/12D3KooWPjX9v7FWmPvAUSTMpG7j2jXWxNnUxyDZrXPqmK29QNEd",
            "/ip4/164.92.188.205/tcp/23001/p2p/12D3KooWJGopzaxs5G2FCPPMAy9zUoEdp5X7VebANvHGwiKW61Ck",
            "/ip4/164.92.188.205/tcp/23002/p2p/12D3KooWPV5c2K5oHugFFArAwaJfxUrrTEjjwJgCQ44LNTsi7p68",
            "/ip4/164.92.188.205/tcp/23003/p2p/12D3KooWKZBm2JvZE9SyPy3bQupgxtHzJC4y3E7xaHX5aRVewspC",
            // gnosis prod
            // "/ip4/139.59.130.109/tcp/23003/p2p/12D3KooWRZoofMsnpsjkgvfPQUyGXZQnn7EVnb4tw4ghNfwMnnsj",
            // "/ip4/167.71.169.248/tcp/23003/p2p/12D3KooWGH3VxoSQXZ6wUuCmsv5caGQnhwfGejbkXH6uS2r7sehA",
            // "/ip4/139.59.130.109/tcp/23003/p2p/12D3KooWNxTiw7CvD1fuyye5P8qPhKTTrRBW6wwZwMdqdTxjYF2H",
            // "/ip4/178.128.192.239/tcp/23003/p2p/12D3KooWCdpkipTiuzVMfkV7yLLgqbFeAL8WmEP78hCoBGBYLugN",
            // "/ip4/45.55.192.248/tcp/23003/p2p/12D3KooWMPuubKqksfMxvLwEBDScaopTdvPLr5J5SMmBEo2zkcMz",
            // "/ip4/178.128.126.237/tcp/23003/p2p/12D3KooWAg1pGUDAfFWSZftpN3JjBfLUCGLQcZApJHv2VntdMS9U"
        ];

        new ShutterP2P().Start(p2pAddresses);

        Console.ReadLine();
    }

    public void Start(in IEnumerable<string> p2pAddresses)
    {
        _serviceProvider = new ServiceCollection()
                    .AddLibp2p(builder => builder)
                    .AddSingleton(new IdentifyProtocolSettings
                    {
                        ProtocolVersion = ShutterP2PProtocolVersion,
                        AgentVersion = ShutterP2PAgentVersion
                    })
                    .AddSingleton(new Settings()
                    {
                        ReconnectionAttempts = int.MaxValue
                    })
                    .AddLogging(builder =>
        builder.SetMinimumLevel(LogLevel.Trace)
                        .AddSimpleConsole(l =>
                        {
                            l.SingleLine = true;
                            l.TimestampFormat = "[HH:mm:ss.FFF]";
                        })
                    )
                    .BuildServiceProvider();

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + ShutterP2PPort);
        Console.WriteLine($"Started Shutter P2P: {peer.Address}");
        _router = _serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.Subscribe("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Writer.TryWrite(msg);
        };

        MyProto proto = new();
        _cancellationTokenSource = new();
        _ = _router.RunAsync(peer, proto, token: _cancellationTokenSource.Token);
        ConnectToPeers(proto, p2pAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long delta = 0;

        Task.Run(async () =>
                {
                    for (; ; )
                    {
                        await Task.Delay(250);

                        while (_msgQueue.Reader.TryRead(out var msg))
                        {
                            Console.WriteLine($"received decryption keys!");
                            lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                        }

                        long oldDelta = delta;
                        delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;

                        if (delta > 0 && delta % (60 * 2) == 0 && delta != oldDelta)
                        {
                            Console.Error.Write($"Not receiving Shutter messages ({delta / 60}m)...");
                        }
                    }
                }, _cancellationTokenSource.Token);
    }

    public void DisposeAsync()
    {
        _router?.UnsubscribeAll();
        _ = _serviceProvider?.DisposeAsync();
        _cancellationTokenSource?.Cancel();
    }

    internal class MyProto : IDiscoveryProtocol
    {
        public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
        public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

        public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
        {
            return Task.Delay(int.MaxValue);
        }
    }

    internal void ConnectToPeers(MyProto proto, IEnumerable<string> p2pAddresses)
    {
        foreach (string addr in p2pAddresses)
        {
            proto.OnAddPeer?.Invoke([addr]);
        }
    }
}
