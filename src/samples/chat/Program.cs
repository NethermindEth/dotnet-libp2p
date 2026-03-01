// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder.WithQuic().AddProtocol<ChatProtocol>())
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Trace)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Chat");
IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

CancellationTokenSource ts = new();

if (args.Length > 0 && args[0] == "-d")
{
    Multiaddress remoteAddr = args[1];

    await using ILocalPeer localPeer = peerFactory.Create();

    logger.LogInformation("Dialing {remote}", remoteAddr);
    ISession remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

    await remotePeer.DialAsync<ChatProtocol>(ts.Token);
}
else
{
    Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
    await using ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    string addrTemplate = args.Contains("-quic") ?
        "/ip4/0.0.0.0/udp/{0}/quic-v1" :
        "/ip4/0.0.0.0/tcp/{0}";

    peer.ListenAddresses.CollectionChanged += (_, args) =>
    {
        if (args.NewItems is { Count: > 0 })
        {
            logger.LogInformation("Listen on {localAddr}", args.NewItems[0]);
        }
    };

    peer.OnConnected += async newSession => logger.LogInformation("A peer connected {remote}", newSession.RemoteAddress);

    int indexOfPort = Array.IndexOf(args, "-sp");

    await peer.StartListenAsync(
        [string.Format(addrTemplate, indexOfPort > -1 ? args[indexOfPort + 1] : "0")],
        ts.Token);
    logger.LogInformation("Listener started at {address}", string.Join(", ", peer.ListenAddresses));

    Console.CancelKeyPress += delegate { ts.Cancel(); };

    await Task.Delay(-1, ts.Token);
}
