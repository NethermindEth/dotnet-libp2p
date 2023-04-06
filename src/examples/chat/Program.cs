// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Builder;
using Nethermind.Libp2p.Core;

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
    MultiAddr remoteAddr = args[1];
    ILocalPeer localPeer = peerFactory.Create();

    logger.LogInformation("Dialing {0}", remoteAddr);
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

    await remotePeer.DialAsync<ChatProtocol>(ts.Token);
    await remotePeer.DisconnectAsync();
}
else
{
    Identity optionalFixedIdentity = Identity.FromPrivateKey(Enumerable.Repeat((byte)42, 32).ToArray());
    ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    IListener listener = await peer.ListenAsync(
        $"/ip4/0.0.0.0/tcp/{(args.Length > 0 && args[0] == "-sp" ? args[1] : "0")}/p2p/{peer.Identity.PeerId}",
        ts.Token);
    logger.LogInformation($"Listener started at {listener.Address}");
    listener.OnConnection += async remotePeer => logger.LogInformation($"A peer connected {remotePeer.Address}");
    Console.CancelKeyPress += delegate { listener.DisconnectAsync(); };

    await listener;
}
