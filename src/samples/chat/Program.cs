// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

var chatProtocol = new ChatProtocol() { OnServerMessage = (msg) => Console.WriteLine("AI: {0}", msg) };

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole(); // works on Windows/macOS
        logging.SetMinimumLevel(LogLevel.Trace);
    })
    .AddLibp2p(builder => ((Libp2pPeerFactoryBuilder)builder).WithQuic().AddProtocol(chatProtocol))
    .BuildServiceProvider();

IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

CancellationTokenSource ts = new();

Multiaddress remoteAddr = "/ip4/139.177.181.61/tcp/42000/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL";
//Multiaddress remoteAddr = "/ip4/139.177.181.61/udp/42000/quic-v1/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL";

await using ILocalPeer localPeer = peerFactory.Create();

ISession remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

await remotePeer.DialAsync<ChatProtocol>(ts.Token);

Console.WriteLine("System: {0}", "Connected {}");
await Task.Delay(-1, ts.Token);

