// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using DataTransferBenchmark;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Nethermind.Libp2p;

await Task.Delay(1000);
{
    ServiceProvider serviceProvider = new ServiceCollection()
        .AddLibp2p(builder => builder.AddProtocol<PerfProtocol>())
        //.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information).AddSimpleConsole(l=>l.SingleLine = true))
        .BuildServiceProvider();

    IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

    Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
    ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    await peer.StartListenAsync([$"/ip4/127.0.0.1/tcp/0/p2p/{peer.Identity.PeerId}"]);

    Multiaddress remoteAddr = peer.ListenAddresses.First();
    ILocalPeer localPeer = peerFactory.Create();
    ISession remotePeer = await localPeer.DialAsync(remoteAddr);

    Stopwatch timeSpent = Stopwatch.StartNew();
    await remotePeer.DialAsync<PerfProtocol>();
    TimeSpan elapsed = timeSpent.Elapsed;
    Console.WriteLine("Libp2p");
    Console.WriteLine("Elapsed {0}", timeSpent.Elapsed);
    Console.WriteLine("Speed {0:0.00} MiB/s", PerfProtocol.TotalLoad / timeSpent.Elapsed.TotalMilliseconds * 1000 / 1024 / 1024);
    await remotePeer.DisconnectAsync();
}
await Task.Delay(1000);

// {
//     IPeerFactory peerFactory = NoStackPeerFactoryBuilder.Create
//         .AddProtocol<PerfProtocol>()
//         .Build();

//     ILocalPeer peer = peerFactory.Create();
//     await peer.StartListenAsync([$"/ip4/0.0.0.0/tcp/0"]);

//     Multiaddress remoteAddr = peer.ListenAddresses.First();
//     ILocalPeer localPeer = peerFactory.Create();
//     ISession remotePeer = await localPeer.DialAsync(remoteAddr);

//     Stopwatch timeSpent = Stopwatch.StartNew();
//     await remotePeer.DialAsync<PerfProtocol>();
//     TimeSpan elapsed = timeSpent.Elapsed;
//     Console.WriteLine("NoStack");
//     Console.WriteLine("Elapsed {0}", timeSpent.Elapsed);
//     Console.WriteLine("Speed {0:0.00} MiB/s", PerfProtocol.TotalLoad / timeSpent.Elapsed.TotalMilliseconds * 1000 / 1024 / 1024);
//     await remotePeer.DisconnectAsync();
// }
