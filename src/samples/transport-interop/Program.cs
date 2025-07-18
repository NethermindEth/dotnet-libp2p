// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;


try
{
    string transport = Environment.GetEnvironmentVariable("transport")!;
    string muxer = Environment.GetEnvironmentVariable("muxer")!;
    string security = Environment.GetEnvironmentVariable("security")!;

    bool isDialer = bool.Parse(Environment.GetEnvironmentVariable("is_dialer")!);
    string ip = Environment.GetEnvironmentVariable("ip") ?? "0.0.0.0";

    string redisAddr = Environment.GetEnvironmentVariable("redis_addr") ?? "redis:6379";

    int testTimeoutSeconds = int.Parse(Environment.GetEnvironmentVariable("test_timeout_seconds") ?? "180");

    TestPlansPeerFactoryBuilder builder = new(transport, muxer, security);
    IPeerFactory peerFactory = builder.Build();

    Log($"Connecting to redis at {redisAddr}...");
    ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(redisAddr);
    IDatabase db = redis.GetDatabase();

    if (isDialer)
    {
        ILocalPeer localPeer = peerFactory.Create();

        Log($"Picking an address to dial...");

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        string? listenerAddr = null;
        while ((listenerAddr = await db.ListRightPopAsync("listenerAddr")) is null)
        {
            await Task.Delay(10, cts.Token);
        }

        Log($"Dialing {listenerAddr}...");
        Stopwatch handshakeStartInstant = Stopwatch.StartNew();
        ISession remotePeer = await localPeer.DialAsync((Multiaddress)listenerAddr);

        Stopwatch pingIstant = Stopwatch.StartNew();
        await remotePeer.DialAsync<PingProtocol>();
        long pingRTT = pingIstant.ElapsedMilliseconds;

        long handshakePlusOneRTT = handshakeStartInstant.ElapsedMilliseconds;

        PrintResult($"{{\"handshakePlusOneRTTMillis\": {handshakePlusOneRTT}, \"pingRTTMilllis\": {pingRTT}}}");
        Log("Done");
        return 0;
    }
    else
    {
        if (ip == "0.0.0.0")
        {
            List<NetworkInterface> d = NetworkInterface.GetAllNetworkInterfaces()!
                 .Where(i => i.Name == "eth0" ||
                    (i.OperationalStatus == OperationalStatus.Up &&
                     i.NetworkInterfaceType == NetworkInterfaceType.Ethernet)).ToList();

            IEnumerable<UnicastIPAddressInformation> addresses = NetworkInterface.GetAllNetworkInterfaces()!
                 .Where(i => i.Name == "eth0" ||
                    (i.OperationalStatus == OperationalStatus.Up &&
                     i.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                     i.GetIPProperties().GatewayAddresses.Any())
                 ).First()
                 .GetIPProperties()
                 .UnicastAddresses
                 .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            Log("Available addresses detected, picking the first: " + string.Join(",", addresses.Select(a => a.Address)));
            ip = addresses.First().Address.ToString()!;
        }
        Log("Starting to listen...");
        ILocalPeer localPeer = peerFactory.Create();

        CancellationTokenSource listennTcs = new();
        await localPeer.StartListenAsync([builder.MakeAddress(ip)], listennTcs.Token);
        localPeer.OnConnected += (session) => { Log($"Connected {session.RemoteAddress}"); return Task.CompletedTask; };
        Log($"Listening on {string.Join(", ", localPeer.ListenAddresses)}");
        db.ListRightPush(new RedisKey("listenerAddr"), new RedisValue(localPeer.ListenAddresses.First().ToString()));
        await Task.Delay(testTimeoutSeconds * 1000);
        await listennTcs.CancelAsync();
        return -1;
    }
}
catch (Exception ex)
{
    Log(ex.Message);
    return -1;
}

static void Log(string info) => Console.Error.WriteLine(info);
static void PrintResult(string info) => Console.WriteLine(info);

class TestPlansPeerFactoryBuilder : PeerFactoryBuilderBase<TestPlansPeerFactoryBuilder, PeerFactory>
{
    private readonly string transport;
    private readonly string? muxer;
    private readonly string? security;
    private static IPeerFactoryBuilder? defaultPeerFactoryBuilder;

    public TestPlansPeerFactoryBuilder(string transport, string? muxer, string? security)
        : base(new ServiceCollection()
            .AddLibp2p()
            .AddLogging(builder =>
                builder.SetMinimumLevel(LogLevel.Trace)
                    .AddSimpleConsole(l =>
                    {
                        l.SingleLine = true;
                        l.TimestampFormat = "[HH:mm:ss.FFF]";
                    }))
            .AddScoped(_ => defaultPeerFactoryBuilder!)
            .BuildServiceProvider())
    {
        defaultPeerFactoryBuilder = this;
        this.transport = transport;
        this.muxer = muxer;
        this.security = security;
    }

    private static readonly string[] stacklessProtocols = ["quic", "quic-v1", "webtransport"];

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef[] transportStack = [transport switch
        {
            "tcp" => Get<IpTcpProtocol>(),
            // TODO: Improve QUIC imnteroperability
            "quic-v1" => Get<QuicProtocol>(),
            _ => throw new NotImplementedException(),
        }];

        ProtocolRef[] selector = [Get<MultistreamProtocol>()];
        Connect(transportStack, selector);

        if (!stacklessProtocols.Contains(transport))
        {
            ProtocolRef[] securityStack = [security switch
            {
                "noise" => Get<NoiseProtocol>(),
                _ => throw new NotImplementedException(),
            }];
            ProtocolRef[] muxerStack = [muxer switch
            {
                "yamux" => Get<YamuxProtocol>(),
                _ => throw new NotImplementedException(),
            }];

            selector = Connect(selector, transportStack, [Get<MultistreamProtocol>()], muxerStack, [Get<MultistreamProtocol>()]);
        }

        ProtocolRef[] apps = [Get<IdentifyProtocol>(), Get<PingProtocol>()];
        Connect(selector, apps);

        return transportStack;
    }

    public string MakeAddress(string ip = "0.0.0.0", string port = "0") => transport switch
    {
        "tcp" => $"/ip4/{ip}/tcp/{port}",
        "quic-v1" => $"/ip4/{ip}/udp/{port}/quic-v1",
        _ => throw new NotImplementedException(),
    };
}
