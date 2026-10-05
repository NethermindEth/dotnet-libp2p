// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.AutoTls;
using System.Net;

bool useSecureWebSockets = args.Contains("-wss") || args.Contains("--websocket-secure");
bool useWebSockets = useSecureWebSockets || args.Contains("-ws") || args.Contains("--websocket");
bool useWebRtcDirect = args.Contains("-wrtd") || args.Contains("--webrtc-direct");
bool useRelay = args.Contains("--relay");
bool useAutoTls = args.Contains("--autotls");
bool useAutoTlsStaging = args.Contains("--autotls-staging");
bool useNoise = args.Contains("--noise");
bool echoMode = args.Contains("--echo");
int sendIndex = Array.IndexOf(args, "--send");
bool useEchoProtocol = echoMode || sendIndex > -1;
string? autoTlsEmail = GetOption(args, "--autotls-email");
string? publicIp = GetOption(args, "--public-ip");
string[] announcedAddressArgs = GetOptions(args, "--announce");

if (useSecureWebSockets && !useAutoTls)
{
    throw new InvalidOperationException("Secure WebSocket listeners require --autotls.");
}

ServiceCollection services = new();
services
    .AddLibp2p(builder =>
    {
        if (useWebSockets)
        {
            builder.WithWebSockets();
            if (!useNoise)
            {
                builder.WithPlaintextEnforced();
            }
        }
        else if (useWebRtcDirect)
        {
            builder.WithWebRtcDirect();
        }
        else
        {
            builder.WithQuic();
        }

        if (useRelay)
        {
            builder.WithRelay();
        }

        return useEchoProtocol ? builder.AddProtocol<EchoChatProtocol>() : builder.AddProtocol<ChatProtocol>();
    })
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }));

if (useAutoTls)
{
    services.AddAutoTls(options =>
    {
        if (useAutoTlsStaging)
        {
            options.AcmeDirectoryUrl = AutoTlsOptions.StagingAcmeDirectoryUrl;
        }

        options.ContactEmail = autoTlsEmail;
    });
}

ServiceProvider serviceProvider = services.BuildServiceProvider();
IHostedService[] hostedServices = useAutoTls ? serviceProvider.GetServices<IHostedService>().ToArray() : [];

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Chat");
IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

CancellationTokenSource ts = new();

if (args.Length > 0 && args[0] == "-d")
{
    Multiaddress remoteAddr = args[1];

    await using ILocalPeer localPeer = peerFactory.Create();

    logger.LogInformation("Dialing {remote}", remoteAddr);
    ISession remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

    if (sendIndex > -1)
    {
        string message = sendIndex + 1 < args.Length ? args[sendIndex + 1] : "hello from dotnet-libp2p";
        string response = await remotePeer.DialAsync<EchoChatProtocol, string, string>(message, ts.Token);
        Console.WriteLine(response);
        await remotePeer.DisconnectAsync();
        await serviceProvider.DisposeAsync();
        return;
    }

    await remotePeer.DialAsync<ChatProtocol>(ts.Token);
}
else
{
    Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
    await using ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    if (useAutoTls)
    {
        if (announcedAddressArgs.Length is 0)
        {
            throw new InvalidOperationException("AutoTLS requires at least one --announce <multiaddr> address that p2p-forge can probe.");
        }

        Multiaddress[] announcedAddresses = announcedAddressArgs.Select(Multiaddress.Decode).ToArray();
        serviceProvider.GetRequiredService<ITlsCertificateProvider>().Configure(optionalFixedIdentity, announcedAddresses);
        foreach (IHostedService hostedService in hostedServices)
        {
            await hostedService.StartAsync(ts.Token);
        }
    }

    string addrTemplate = useSecureWebSockets ? "/ip4/0.0.0.0/tcp/{0}/wss"
        : useWebSockets ? "/ip4/0.0.0.0/tcp/{0}/ws"
        : useWebRtcDirect ? "/ip4/0.0.0.0/udp/{0}/webrtc-direct"
        : args.Contains("-quic") ? "/ip4/0.0.0.0/udp/{0}/quic-v1"
        : "/ip4/0.0.0.0/tcp/{0}";

    peer.ListenAddresses.CollectionChanged += (_, eventArgs) =>
    {
        if (eventArgs.NewItems is { Count: > 0 })
        {
            logger.LogInformation("Listen on {localAddr}", eventArgs.NewItems[0]);
        }
    };

    peer.OnConnected += newSession => logger.LogInformation("A peer connected {remote}", newSession.RemoteAddress);

    int indexOfPort = Array.IndexOf(args, "-sp");

    await peer.StartListenAsync(
        [string.Format(addrTemplate, indexOfPort > -1 ? args[indexOfPort + 1] : "0")],
        ts.Token);
    logger.LogInformation("Listener started at {address}", string.Join(", ", peer.ListenAddresses));

    if (useAutoTls && publicIp is not null)
    {
        Multiaddress? listenAddress = peer.ListenAddresses.FirstOrDefault(address => address.Has<TCP>());
        if (listenAddress is not null)
        {
            string host = AutoTlsDomain.GetIpHost(peer.Identity.PeerId, IPAddress.Parse(publicIp));
            logger.LogInformation(
                "Browser WSS address {address}",
                $"/dns4/{host}/tcp/{listenAddress.Get<TCP>()}/wss/p2p/{peer.Identity.PeerId}");
        }
    }

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        ts.Cancel();
    };

    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ts.Token);
    }
    catch (OperationCanceledException) when (ts.IsCancellationRequested)
    {
    }
}

foreach (IHostedService hostedService in hostedServices.Reverse())
{
    await hostedService.StopAsync(CancellationToken.None);
}

await serviceProvider.DisposeAsync();

static string? GetOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index > -1 && index + 1 < args.Length ? args[index + 1] : null;
}

static string[] GetOptions(string[] args, string name)
{
    List<string> values = [];
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length)
        {
            values.Add(args[++i]);
        }
    }
    return values.ToArray();
}
