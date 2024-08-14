// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    TestPlansPeerFactoryBuilder builder = new TestPlansPeerFactoryBuilder(transport, muxer, security);
    IPeerFactory peerFactory = builder.Build();

    Log($"Connecting to redis at {redisAddr}...");
    ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(redisAddr);
    IDatabase db = redis.GetDatabase();

    if (isDialer)
    {
        IPeer localPeer = peerFactory.Create();

        Log($"Picking an address to dial...");

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        string? listenerAddr = null;
        while ((listenerAddr = await db.ListRightPopAsync("listenerAddr")) is null)
        {
            await Task.Delay(10, cts.Token);
        }

        Log($"Dialing {listenerAddr}...");
        Stopwatch handshakeStartInstant = Stopwatch.StartNew();
        ISession remotePeer = await localPeer.DialAsync(listenerAddr);

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
            var d = NetworkInterface.GetAllNetworkInterfaces()!
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
        IPeer localPeer = peerFactory.Create();

        CancellationTokenSource listennTcs = new();
        await localPeer.StartListenAsync([builder.MakeAddress(ip)], listennTcs.Token);
        localPeer.OnConnection += (session) => { Log($"Connected {session.RemoteAddress}"); return Task.CompletedTask; };
        Log($"Listening on {localPeer.Address}");
        db.ListRightPush(new RedisKey("listenerAddr"), new RedisValue(localPeer.Address.ToString()));
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

    protected override ProtocolStack BuildStack()
    {
        ProtocolStack stack = transport switch
        {
            "tcp" => Over<IpTcpProtocol>(),
            // TODO: Improve QUIC imnteroperability
            "quic-v1" => Over<QuicProtocol>(),
            _ => throw new NotImplementedException(),
        };

        stack = stack.Over<MultistreamProtocol>();

        if (!stacklessProtocols.Contains(transport))
        {
            stack = security switch
            {
                "noise" => stack.Over<NoiseProtocol>(),
                _ => throw new NotImplementedException(),
            };
            stack = stack.Over<MultistreamProtocol>();
            stack = muxer switch
            {
                "yamux" => stack.Over<YamuxProtocol>(),
                _ => throw new NotImplementedException(),
            };
            stack = stack.Over<MultistreamProtocol>();
        }

        return stack.AddAppLayerProtocol<IdentifyProtocol>()
                    .AddAppLayerProtocol<PingProtocol>();
    }

    public string MakeAddress(string ip = "0.0.0.0", string port = "0") => transport switch
    {
        "tcp" => $"/ip4/{ip}/tcp/{port}",
        "quic-v1" => $"/ip4/{ip}/udp/{port}/quic-v1",
        _ => throw new NotImplementedException(),
    };
}
