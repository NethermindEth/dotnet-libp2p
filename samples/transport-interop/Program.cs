// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;
using Nethermind.Libp2p.Protocols.WebRtc;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;


try
{
    string transport = GetRequiredEnvironmentVariable("transport", "TRANSPORT").ToLowerInvariant();

    // For QUIC, muxer and security are built-in and not required
    bool isStacklessProtocol = transport is "quic" or "quic-v1" or "webtransport" or "webrtc-direct";

    string muxer = GetOptionalEnvironmentVariable("muxer", "MUXER")?.ToLowerInvariant() ?? "";
    if (string.IsNullOrEmpty(muxer) && !isStacklessProtocol)
    {
        throw new Exception("muxer environment variable is required");
    }
    string security = GetOptionalEnvironmentVariable("security", "SECURE_CHANNEL")?.ToLowerInvariant() ?? "";
    if (string.IsNullOrEmpty(security) && !isStacklessProtocol)
    {
        throw new Exception("security environment variable is required");
    }

    string isDialerValue = GetRequiredEnvironmentVariable("is_dialer", "IS_DIALER");
    if (!bool.TryParse(isDialerValue, out bool isDialer))
    {
        throw new Exception("is_dialer environment variable must be true or false");
    }
    string ip = GetOptionalEnvironmentVariable("ip", "LISTENER_IP") ?? "0.0.0.0";

    string redisAddr = GetOptionalEnvironmentVariable("redis_addr", "REDIS_ADDR") ?? "redis:6379";

    string testTimeoutValue = GetOptionalEnvironmentVariable("test_timeout_seconds", "TEST_TIMEOUT_SECS") ?? "180";
    if (!int.TryParse(testTimeoutValue, out int testTimeoutSeconds))
    {
        throw new Exception("test_timeout_seconds environment variable must be an integer");
    }

    string? testKey = GetOptionalEnvironmentVariable("TEST_KEY");
    string redisKey = string.IsNullOrWhiteSpace(testKey) ? "listenerAddr" : $"{testKey}_listener_multiaddr";

    TestPlansPeerFactoryBuilder builder = new(transport, muxer, security);
    IPeerFactory peerFactory = builder.Build();

    Log($"Connecting to redis at {redisAddr}...");
    ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(redisAddr);
    IDatabase db = redis.GetDatabase();

    if (isDialer)
    {
        ILocalPeer localPeer = peerFactory.Create();

        Log($"Picking an address to dial...");

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(testTimeoutSeconds));
        string? listenerAddr = null;
        while ((listenerAddr = await db.ListLeftPopAsync(redisKey)) is null)
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

        PrintResult(FormattableString.Invariant($@"{{""handshakePlusOneRTTMillis"":{handshakePlusOneRTT},""pingRTTMilllis"":{pingRTT}}}"));
        Log("Done");
        return 0;
    }
    else
    {
        if (ip == "0.0.0.0")
        {
            Log("Auto-detecting network interface...");

            // Get all active network interfaces with IPv4 addresses
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(i => new
                {
                    Interface = i,
                    Addresses = i.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Where(a => !IPAddress.IsLoopback(a.Address))
                        .ToList()
                })
                .Where(x => x.Addresses.Any())
                .ToList();

            Log($"Found {candidates.Count} candidate interfaces: {string.Join(", ", candidates.Select(c => $"{c.Interface.Name}({c.Interface.NetworkInterfaceType})"))}");

            // Priority order: Physical Ethernet > Wi-Fi > Virtual Ethernet > Others
            var selectedInterface = candidates
                .OrderBy(c => GetInterfacePriority(c.Interface))
                .ThenByDescending(c => c.Interface.Speed) // Prefer faster interfaces
                .FirstOrDefault();

            if (selectedInterface == null)
            {
                Log("No suitable network interface found. Available interfaces:");
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    Log($"  {ni.Name}: {ni.NetworkInterfaceType}, {ni.OperationalStatus}");
                }
                throw new Exception("No active network interface with IPv4 address found. Set LISTENER_IP environment variable to specify an IP address.");
            }

            var selectedAddress = selectedInterface.Addresses.First();
            ip = selectedAddress.Address.ToString();
            Log($"Selected interface: {selectedInterface.Interface.Name} ({selectedInterface.Interface.NetworkInterfaceType}) -> {ip}");
        }
        Log("Starting to listen...");
        ILocalPeer localPeer = peerFactory.Create();

        CancellationTokenSource listenTcs = new();
        await localPeer.StartListenAsync([builder.MakeAddress(ip)], listenTcs.Token);
        localPeer.OnConnected += session => Log($"Connected {session.RemoteAddress}");
        Log($"Listening on {string.Join(", ", localPeer.ListenAddresses)}");
        db.ListRightPush(new RedisKey(redisKey), new RedisValue(localPeer.ListenAddresses.First().ToString()));
        await Task.Delay(TimeSpan.FromSeconds(testTimeoutSeconds));
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

static string GetRequiredEnvironmentVariable(params string[] names)
{
    string? value = GetOptionalEnvironmentVariable(names);
    if (value is null)
    {
        throw new Exception($"{string.Join("/", names)} environment variable is required");
    }

    return value;
}

static string? GetOptionalEnvironmentVariable(params string[] names)
{
    foreach (string name in names)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static int GetInterfacePriority(NetworkInterface networkInterface)
{
    // Lower number = higher priority
    return networkInterface.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Ethernet => 1,           // Physical Ethernet (best)
        NetworkInterfaceType.Wireless80211 => 2,      // Wi-Fi (good)
        NetworkInterfaceType.GigabitEthernet => 1,     // Gigabit Ethernet (best)
        NetworkInterfaceType.FastEthernetT => 1,       // Fast Ethernet (best)
        NetworkInterfaceType.Ethernet3Megabit => 1,    // Legacy Ethernet (best)
        NetworkInterfaceType.GenericModem => 5,        // Modem (low priority)
        NetworkInterfaceType.Ppp => 5,                 // PPP connection (low priority)
        NetworkInterfaceType.Tunnel => 4,              // Tunnel interface (medium-low)
        _ when networkInterface.Name.Contains("vEthernet") => 3, // Hyper-V virtual (medium)
        _ when networkInterface.Name.Contains("VirtualBox") => 3, // VirtualBox (medium)
        _ when networkInterface.Name.Contains("VMware") => 3,     // VMware (medium)
        _ when networkInterface.Description.Contains("Virtual") => 3, // Other virtual (medium)
        _ => 6  // Unknown/other (lowest priority)
    };
}

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

    private static readonly string[] stacklessProtocols = ["quic", "quic-v1", "webtransport", "webrtc-direct"];

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef transport = _transport switch
        {
            "tcp" => Get<IpTcpProtocol>(),
            "quic" => Get<QuicProtocol>(),
            "quic-v1" => Get<QuicProtocol>(),
            "webrtc-direct" => Get<WebRtcDirectProtocol>(),
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
                "tls" => Get<TlsProtocol>(),
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
        "quic" => $"/ip4/{ip}/udp/{port}/quic-v1",
        "quic-v1" => $"/ip4/{ip}/udp/{port}/quic-v1",
        "webrtc-direct" => $"/ip4/{ip}/udp/{port}/webrtc-direct",
        _ => throw new NotImplementedException(),
    };
}
