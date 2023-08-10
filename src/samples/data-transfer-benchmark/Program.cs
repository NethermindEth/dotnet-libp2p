// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using DataTransferBenchmark;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Builder;
using Nethermind.Libp2p.Core;

TaskScheduler.UnobservedTaskException += (s, e) =>
{

};

await Task.Delay(1000);
{
    ServiceProvider serviceProvider = new ServiceCollection()
        .AddLibp2p(builder => builder.AddAppLayerProtocol<DataTransferBenchmarkProtocol>())
        //.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information).AddSimpleConsole(l=>l.SingleLine = true))
        .BuildServiceProvider();

    IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

    Identity optionalFixedIdentity = Identity.FromPrivateKey(Enumerable.Repeat((byte)42, 32).ToArray());
    ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    IListener listener = await peer.ListenAsync($"/ip4/0.0.0.0/tcp/0/p2p/{peer.Identity.PeerId}");

    MultiAddr remoteAddr = listener.Address;
    ILocalPeer localPeer = peerFactory.Create();
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr);

    Stopwatch timeSpent = Stopwatch.StartNew();
    await remotePeer.DialAsync<DataTransferBenchmarkProtocol>();
    TimeSpan elapsed = timeSpent.Elapsed;
    Console.WriteLine("Libp2p");
    Console.WriteLine("Elapsed {0}", timeSpent.Elapsed);
    Console.WriteLine("Speed {0:0.00} MiB/s", DataTransferBenchmarkProtocol.TotalLoad / timeSpent.Elapsed.TotalMilliseconds * 1000 / 1024 / 1024);
    await remotePeer.DisconnectAsync();
}
await Task.Delay(1000);

{
    IPeerFactory peerFactory = NoStackPeerFactoryBuilder.Create
        .AddAppLayerProtocol<DataTransferBenchmarkProtocol>()
        .Build();

    ILocalPeer peer = peerFactory.Create();
    IListener listener = await peer.ListenAsync($"/ip4/0.0.0.0/tcp/0");

    MultiAddr remoteAddr = listener.Address;
    ILocalPeer localPeer = peerFactory.Create();
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr);

    Stopwatch timeSpent = Stopwatch.StartNew();
    await remotePeer.DialAsync<DataTransferBenchmarkProtocol>();
    TimeSpan elapsed = timeSpent.Elapsed;
    Console.WriteLine("NoStack");
    Console.WriteLine("Elapsed {0}", timeSpent.Elapsed);
    Console.WriteLine("Speed {0:0.00} MiB/s", DataTransferBenchmarkProtocol.TotalLoad / timeSpent.Elapsed.TotalMilliseconds * 1000 / 1024 / 1024);
    await remotePeer.DisconnectAsync();
}
