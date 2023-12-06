// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Multiformats.Address.Protocols;

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder.AddAppLayerProtocol<ChatProtocol>())
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Chat");
CancellationTokenSource ts = new();

if (args.Length > 0 && args[0] == "-d")
{
    Multiaddress remoteAddr = args[1];

    string addrTemplate = remoteAddr.Has<QUICv1>() ?
       "/ip4/0.0.0.0/udp/0/quic-v1" :
       "/ip4/0.0.0.0/tcp/0";

    ILocalPeer localPeer = peerFactory.Create(localAddr: addrTemplate);

    logger.LogInformation("Dialing {0}", remoteAddr);
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

    await remotePeer.DialAsync<ChatProtocol>(ts.Token);
    await remotePeer.DisconnectAsync();
}
else
{
    Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
    ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    string addrTemplate = args.Contains("-quic") ?
        "/ip4/0.0.0.0/udp/{0}/quic-v1/p2p/{1}" :
        "/ip4/0.0.0.0/tcp/{0}/p2p/{1}";

    IListener listener = await peer.ListenAsync(
        string.Format(addrTemplate, args.Length > 0 && args[0] == "-sp" ? args[1] : "0", peer.Identity.PeerId),
        ts.Token);
    logger.LogInformation($"Listener started at {listener.Address}");
    listener.OnConnection += async remotePeer => logger.LogInformation($"A peer connected {remotePeer.Address}");
    Console.CancelKeyPress += delegate { listener.DisconnectAsync(); };

    await listener;
}
