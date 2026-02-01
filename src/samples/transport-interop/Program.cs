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
    string transport = Environment.GetEnvironmentVariable("TRANSPORT")!;
    if (string.IsNullOrEmpty(transport))
    {
        throw new Exception("TRANSPORT environment variable is required");
    }
    
    // For QUIC, muxer and security are built-in and not required
    bool isStacklessProtocol = transport == "quic-v1" || transport == "webtransport";
    
    string muxer = Environment.GetEnvironmentVariable("MUXER") ?? "";
    if (string.IsNullOrEmpty(muxer) && !isStacklessProtocol)
    {
        throw new Exception("MUXER environment variable is required");
    }
    string security = Environment.GetEnvironmentVariable("SECURE_CHANNEL") ?? "";
    if (string.IsNullOrEmpty(security) && !isStacklessProtocol)
    {
        throw new Exception("SECURE_CHANNEL environment variable is required");
    }

    bool isDialer = bool.Parse(Environment.GetEnvironmentVariable("IS_DIALER")!);
     if (string.IsNullOrEmpty(isDialer.ToString()))
    {
        throw new Exception("IS_DIALER environment variable is required");
    }
    string ip = Environment.GetEnvironmentVariable("LISTENER_IP") ?? "0.0.0.0";

    string redisAddr = Environment.GetEnvironmentVariable("REDIS_ADDR") ?? "";

    int testTimeoutSeconds = int.Parse(Environment.GetEnvironmentVariable("TEST_TIMEOUT_SECS") ?? "180");

    string testKey= Environment.GetEnvironmentVariable("TEST_KEY") ?? "";
     if (string.IsNullOrEmpty(testKey))
    {
        throw new Exception("TEST_KEY environment variable is required");
    }
    string redisKey = $"{testKey}_listener_multiaddr";    

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
        while ((listenerAddr = await db.ListRightPopAsync(redisKey)) is null)
        {
            await Task.Delay(10, cts.Token);
        }

        Log($"Dialing {listenerAddr}...");
        Stopwatch handshakeStartInstant = Stopwatch.StartNew();
        ISession remotePeer = await localPeer.DialAsync((Multiaddress)listenerAddr);

        Stopwatch pingTimeSpent = Stopwatch.StartNew();
        await remotePeer.DialAsync<PingProtocol>();
        long pingRTT = pingTimeSpent.ElapsedMilliseconds;

        long handshakePlusOneRTT = handshakeStartInstant.ElapsedMilliseconds;

        PrintResult("latency:");
        PrintResult($"  handshake_plus_one_rtt: {handshakePlusOneRTT}");
        PrintResult($"  ping_rtt: {pingRTT}");
        PrintResult("  unit: ms");
        Log("Done");
        return 0;
    }
    else
    {
        if (ip == "0.0.0.0")
        {
            List<NetworkInterface> interfaces = NetworkInterface.GetAllNetworkInterfaces()!
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

        CancellationTokenSource listenTcs = new();
        await localPeer.StartListenAsync([builder.MakeAddress(ip)], listenTcs.Token);
        localPeer.OnConnected += (session) => { Log($"Connected {session.RemoteAddress}"); return Task.CompletedTask; };
        Log($"Listening on {string.Join(", ", localPeer.ListenAddresses)}");
        db.ListRightPush(new RedisKey(redisKey), new RedisValue(localPeer.ListenAddresses.First().ToString()));
        await Task.Delay(testTimeoutSeconds * 1000);
        await listenTcs.CancelAsync();
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
    private readonly string _transport;
    private readonly string? _muxer;
    private readonly string? _encryption;

    public TestPlansPeerFactoryBuilder(string transport, string? muxer, string? encryption)
        : base(new ServiceCollection()
            .AddLibp2p<TestPlansPeerFactoryBuilder>()
            .AddLogging(builder =>
                builder.SetMinimumLevel(LogLevel.Trace)
                    .AddSimpleConsole(l =>
                    {
                        l.SingleLine = true;
                        l.TimestampFormat = "[HH:mm:ss.FFF]";
                    }))
            .BuildServiceProvider())
    {
        _transport = transport;
        _muxer = muxer;
        _encryption = encryption;
    }

    private static readonly string[] stacklessProtocols = ["quic-v1", "webtransport"];

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef transport = _transport switch
        {
            "tcp" => Get<IpTcpProtocol>(),
            // TODO: Improve QUIC interoperability
            "quic-v1" => Get<QuicProtocol>(),
            _ => throw new NotImplementedException(),
        };

        ProtocolRef[] selector = null!;

        if (stacklessProtocols.Contains(_transport))
        {
            selector = Connect(transport, Get<MultistreamProtocol>());
        }
        else
        {
            ProtocolRef encryption = _encryption switch
            {
                "noise" => Get<NoiseProtocol>(),
                _ => throw new NotImplementedException(),
            };
            ProtocolRef muxer = _muxer switch
            {
                "yamux" => Get<YamuxProtocol>(),
                _ => throw new NotImplementedException(),
            };

            selector = Connect(transport, Get<MultistreamProtocol>(), encryption, Get<MultistreamProtocol>(), muxer, Get<MultistreamProtocol>());
        }

        ProtocolRef[] apps = [Get<IdentifyProtocol>(), Get<PingProtocol>()];
        Connect(selector, apps);

        return transport;
    }

    public string MakeAddress(string ip = "0.0.0.0", string port = "0") => _transport switch
    {
        "tcp" => $"/ip4/{ip}/tcp/{port}",
        "quic-v1" => $"/ip4/{ip}/udp/{port}/quic-v1",
        _ => throw new NotImplementedException(),
    };
}
