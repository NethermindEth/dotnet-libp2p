// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

var chatProtocol = new ChatProtocol();

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder.WithQuic().AddProtocol(chatProtocol))
    .AddLogging(builder =>
        builder.SetMinimumLevel(LogLevel.Trace)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Chat");
IPeerFactory peerFactory = serviceProvider.GetRequiredService<IPeerFactory>();

using CancellationTokenSource ts = new();

bool quicOnly = args.Contains("--quic-only") || args.Contains("-quic");
bool tcpOnly = args.Contains("--tcp-only") || args.Contains("-tcp");
if (quicOnly && tcpOnly)
{
    throw new ArgumentException("Use either --quic-only or --tcp-only, not both.");
}

int indexOfPort = Array.IndexOf(args, "-sp");
if (indexOfPort < 0)
{
    indexOfPort = Array.IndexOf(args, "--server-port");
}

string port = indexOfPort >= 0 && indexOfPort < args.Length - 1 ? args[indexOfPort + 1] : "42000";

Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
await using ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

List<Multiaddress> listenAddresses = [];
if (!tcpOnly)
{
    listenAddresses.Add($"/ip4/0.0.0.0/udp/{port}/quic-v1");
}

if (!quicOnly)
{
    listenAddresses.Add($"/ip4/0.0.0.0/tcp/{port}");
}

peer.ListenAddresses.CollectionChanged += (_, args) =>
{
    if (args.NewItems is { Count: > 0 })
    {
        logger.LogInformation("Listen on {localAddr}", args.NewItems[0]);
    }
};

peer.OnConnected += newSession => logger.LogInformation("A peer connected {remote}", newSession.RemoteAddress);

await peer.StartListenAsync([.. listenAddresses], ts.Token);
logger.LogInformation("Listener started at {address}", string.Join(", ", peer.ListenAddresses));

Console.CancelKeyPress += delegate { ts.Cancel(); };

await Task.Delay(-1, ts.Token);
