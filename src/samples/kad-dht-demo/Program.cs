// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

namespace KadDhtDemo;

internal static class Program
{
    private record CliConfig(
        List<string> BootstrapAddresses,
        List<string>? ListenAddresses,
        bool NoRemoteBootstrap,
        bool ShowHelp);

    static async Task Main(string[] args)
    {
        var config = ParseArgs(args);
        if (config.ShowHelp)
        {
            ShowUsage();
            return;
        }

        // Logging: console + file
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
            builder.AddProvider(new SimpleFileLoggerProvider("kad-dht-demo.log"));
        });
        var logger = loggerFactory.CreateLogger("KadDhtDemo");

        //  Build DI container
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddConsole());

        // Protocol library does all the heavy lifting:
        //   KadDhtProtocol with its own KBucketTree routing table,
        //   SharedDhtState, message senders, value/provider stores.
        services.AddKadDht(options =>
        {
            options.Mode = KadDhtMode.Server;
            options.KSize = 20;
            options.Alpha = 10;
            options.OperationTimeout = TimeSpan.FromSeconds(30);
        });

        services.AddLibp2p(builder => builder.WithKadDht());

        // Register ILocalPeer so the KadDhtProtocol singleton can resolve it.
        var identity = new Identity();
        services.AddSingleton<ILocalPeer>(sp =>
            sp.GetRequiredService<IPeerFactory>().Create(identity));

        var serviceProvider = services.BuildServiceProvider();

        // Create local peer and resolve KadDHT services
        var localPeer = serviceProvider.GetRequiredService<ILocalPeer>();
        var kadProtocol = serviceProvider.GetRequiredService<KadDhtProtocol>();
        var sharedState = serviceProvider.GetRequiredService<SharedDhtState>();

        //  Auto-add inbound peers to the routing table
        //  MUST be registered BEFORE StartListenAsync to avoid missing early connections.
        localPeer.OnConnected += session =>
        {
            var remotePeerId = session.RemoteAddress?.GetPeerId();
            if (remotePeerId is not null)
            {
                logger.LogInformation("Peer connected: {PeerId} from {Address}",
                    remotePeerId, session.RemoteAddress);

                kadProtocol.AddNode(new DhtNode
                {
                    PeerId = remotePeerId,
                    PublicKey = new PublicKey(remotePeerId.Bytes.ToArray()),
                    Multiaddrs = session.RemoteAddress is not null
                        ? [session.RemoteAddress.ToString()]
                        : Array.Empty<string>()
                });
            }
            return Task.CompletedTask;
        };

        // Start listening — KadDhtProtocol will pick up ListenAddresses once they're populated
        var listenAddrs = BuildListenAddresses(config.ListenAddresses).ToArray();
        await localPeer.StartListenAsync(listenAddrs, CancellationToken.None);

        // ── Startup banner ───────────────────────────────────────────────
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              Kademlia DHT Demo — libp2p                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine($"\nPeer ID: {localPeer.Identity.PeerId}");
        Console.WriteLine("\nListening on:");
        foreach (var addr in localPeer.ListenAddresses)
        {
            Console.WriteLine($"  {AppendPeerId(addr, localPeer.Identity.PeerId)}");
        }

        // Show reachable LAN addresses for cross-machine connectivity
        var lanAddresses = GetAdvertisedAddresses(localPeer);
        if (lanAddresses.Count > 0)
        {
            Console.WriteLine("\nReachable from other machines:");
            foreach (var addr in lanAddresses)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  {addr}");
                Console.ResetColor();
            }
        }

        // Always show LAN IPs so users know what to dial from another PC
        var lanIps = GetLanIPv4Addresses();
        if (lanIps.Count > 0)
        {
            var port = GetListenPort(localPeer);
            var boundToAll = lanAddresses.Count > 0;
            Console.WriteLine($"\nLAN addresses (IPv4):");
            foreach (var ip in lanIps)
            {
                var fullAddr = $"/ip4/{ip}/tcp/{port}/p2p/{localPeer.Identity.PeerId}";
                if (boundToAll)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  ✓ {fullAddr}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ✗ {fullAddr}  (use --listen /ip4/0.0.0.0/tcp/{port} to enable)");
                }
                Console.ResetColor();
            }
        }

        // Connect to bootstrap peers 
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (config.BootstrapAddresses.Count > 0)
        {
            Console.WriteLine($"\nConnecting to {config.BootstrapAddresses.Count} bootstrap peer(s)...");
            foreach (var addrStr in config.BootstrapAddresses)
            {
                try
                {
                    var ma = Multiaddress.Decode(addrStr);
                    await localPeer.DialAsync(ma, cts.Token);

                    // Add to routing table so bootstrap lookup has a seed
                    var remotePeerId = ma.GetPeerId();
                    if (remotePeerId is not null)
                    {
                        kadProtocol.AddNode(new DhtNode
                        {
                            PeerId = remotePeerId,
                            PublicKey = new PublicKey(remotePeerId.Bytes.ToArray()),
                            Multiaddrs = [StripPeerIdComponent(addrStr)]
                        });
                    }
                    Console.WriteLine($"  Connected: {addrStr}");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  Failed: {addrStr} -- {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        // DHT bootstrap (self-lookup to discover more peers)
        Console.WriteLine("\nRunning DHT bootstrap (self-lookup)...");
        try
        {
            await kadProtocol.BootstrapAsync(cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bootstrap completed with errors: {Error}", ex.Message);
        }
        Console.WriteLine($"Routing table: {sharedState.PeerCount} peer(s)");

        // Background maintenance (refresh, republish, cleanup)
        _ = Task.Run(() => kadProtocol.RunAsync(cts.Token), cts.Token);

        // Interactive menu
        await RunInteractiveMenu(localPeer, kadProtocol, sharedState, cts.Token);
    }

    //  Interactive menu

    private static async Task RunInteractiveMenu(
        ILocalPeer localPeer,
        KadDhtProtocol kadProtocol,
        SharedDhtState sharedState,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Console.WriteLine("\n────────────────────────────────────────────");
            Console.WriteLine("  [1] Routing table status");
            Console.WriteLine("  [2] Store a value  (PUT_VALUE)");
            Console.WriteLine("  [3] Retrieve a value  (GET_VALUE)");
            Console.WriteLine("  [4] Run automated test");
            Console.WriteLine("  [5] DHT statistics");
            Console.WriteLine("  [6] Quit");
            Console.WriteLine("────────────────────────────────────────────");
            Console.Write("Choice: ");

            var choice = Console.ReadLine()?.Trim();
            try
            {
                switch (choice)
                {
                    case "1": ShowRoutingTableStatus(localPeer, sharedState, kadProtocol); break;
                    case "2": await StoreDhtValue(kadProtocol, token); break;
                    case "3": await RetrieveDhtValue(kadProtocol, token); break;
                    case "4": await RunAutomatedTest(localPeer, kadProtocol, sharedState, token); break;
                    case "5": ShowDhtStatistics(localPeer, kadProtocol, sharedState); break;
                    case "6": return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    //  [1] Routing table

    private static void ShowRoutingTableStatus(
        ILocalPeer localPeer,
        SharedDhtState sharedState,
        KadDhtProtocol kadProtocol)
    {
        Console.WriteLine("\n  ROUTING TABLE");
        Console.WriteLine("  ════════════════════════════════════════════════");
        Console.WriteLine($"  Local Peer:       {localPeer.Identity.PeerId}");
        Console.WriteLine($"  Total peers:      {sharedState.PeerCount}");

        var routingTable = kadProtocol.RoutingTable;
        if (routingTable is null)
        {
            Console.WriteLine("  (routing table not initialised)");
            return;
        }

        // Show bucket summary (efficient — no 1-256 distance iteration)
        var buckets = routingTable.IterateBuckets().ToList();
        Console.WriteLine($"  Buckets:          {buckets.Count}");

        if (buckets.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Distance  | Peers");
            Console.WriteLine("  ──────────┼──────────────────────────────");
            foreach (var (_, distance, bucket) in buckets)
            {
                var peers = bucket.GetAll();
                if (peers.Length == 0) continue;
                Console.Write($"  {distance,8}  | ");
                Console.WriteLine(string.Join(", ",
                    peers.Take(3).Select(p => TruncatePeerId(p.PeerId))));
                if (peers.Length > 3)
                    Console.WriteLine($"            |   ... +{peers.Length - 3} more");
            }
        }
        Console.WriteLine("  ════════════════════════════════════════════════");
    }

    //  [2] Store value

    private static async Task StoreDhtValue(KadDhtProtocol kadProtocol, CancellationToken token)
    {
        Console.Write("\n  Key: ");
        var key = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(key)) { Console.WriteLine("  (cancelled)"); return; }

        Console.Write("  Value: ");
        var value = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(value)) { Console.WriteLine("  (cancelled)"); return; }

        Console.WriteLine($"  Storing '{key}' = '{value}' ...");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);

        var success = await kadProtocol.PutValueAsync(keyBytes, valueBytes, token);

        Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(success
            ? "  Stored on DHT network"
            : "  Stored locally only (no peers reachable)");
        Console.ResetColor();
    }

    //  [3] Retrieve value

    private static async Task RetrieveDhtValue(KadDhtProtocol kadProtocol, CancellationToken token)
    {
        Console.Write("\n  Key: ");
        var key = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(key)) { Console.WriteLine("  (cancelled)"); return; }

        Console.WriteLine($"  Looking up '{key}' ...");
        var keyBytes = Encoding.UTF8.GetBytes(key);

        var result = await kadProtocol.GetValueAsync(keyBytes, token);

        if (result is not null)
        {
            var valueStr = Encoding.UTF8.GetString(result);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Found: '{valueStr}'");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Not found");
        }
        Console.ResetColor();
    }

    // [4] Automated test 

    private static async Task RunAutomatedTest(
        ILocalPeer localPeer,
        KadDhtProtocol kadProtocol,
        SharedDhtState sharedState,
        CancellationToken token)
    {
        Console.WriteLine("\n  AUTOMATED DHT TEST");
        Console.WriteLine("  ════════════════════════════════════════════════");

        var testData = new Dictionary<string, string>
        {
            ["greeting"] = $"Hello from {TruncatePeerId(localPeer.Identity.PeerId)}",
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["message"] = "DHT test message",
            ["data"] = "Sample data for distributed hash table"
        };

        // 1. Store values
        Console.WriteLine("\n  1. Storing test values...");
        int putSuccess = 0;
        foreach (var kvp in testData)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kvp.Key);
            var valueBytes = Encoding.UTF8.GetBytes(kvp.Value);
            var ok = await kadProtocol.PutValueAsync(keyBytes, valueBytes, token);
            var status = ok ? "replicated" : "local only";
            Console.WriteLine($"     '{kvp.Key}' = '{kvp.Value}' [{status}]");
            if (ok) putSuccess++;
            await Task.Delay(100, token);
        }

        // 2. Retrieve values
        Console.WriteLine("\n  2. Retrieving test values...");
        int getSuccess = 0;
        foreach (var kvp in testData)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kvp.Key);
            var result = await kadProtocol.GetValueAsync(keyBytes, token);
            if (result is not null)
            {
                var found = Encoding.UTF8.GetString(result);
                var match = found == kvp.Value;
                Console.ForegroundColor = match ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.WriteLine($"     '{kvp.Key}': {(match ? "OK" : $"MISMATCH (got '{found}')")}");
                Console.ResetColor();
                if (match) getSuccess++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"     '{kvp.Key}': not found");
                Console.ResetColor();
            }
        }

        // 3. Bootstrap lookup
        Console.WriteLine("\n  3. Bootstrap lookup...");
        try
        {
            await kadProtocol.BootstrapAsync(token);
            Console.WriteLine($"     Routing table after bootstrap: {sharedState.PeerCount} peer(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     Bootstrap error: {ex.Message}");
        }

        // Summary
        Console.WriteLine("\n  ────────────────────────────────────────────────");
        Console.WriteLine($"  PUT: {putSuccess}/{testData.Count} replicated to peers");
        Console.WriteLine($"  GET: {getSuccess}/{testData.Count} retrieved correctly");
        Console.WriteLine($"  Routing table: {sharedState.PeerCount} peer(s)");
        Console.WriteLine("  ════════════════════════════════════════════════");

        Console.WriteLine("\n  MULTI-NODE TESTING:");
        Console.WriteLine("  ─────────────────────────────────────────────────");
        Console.WriteLine("  1. Start listener:  dotnet run -- --listen /ip4/127.0.0.1/tcp/40001");
        Console.WriteLine("  2. Start dialer:    dotnet run -- --listen /ip4/127.0.0.1/tcp/40002 \\");
        Console.WriteLine("                        --bootstrap /ip4/127.0.0.1/tcp/40001/p2p/<PeerID>");
        Console.WriteLine("  3. On dialer [2] PUT, on listener [3] GET to verify DHT replication.");
        Console.WriteLine("  ─────────────────────────────────────────────────");
    }

    // [5] Statistics

    private static void ShowDhtStatistics(
        ILocalPeer localPeer,
        KadDhtProtocol kadProtocol,
        SharedDhtState sharedState)
    {
        Console.WriteLine("\n  DHT STATISTICS");
        Console.WriteLine("  ════════════════════════════════════════════════");

        Console.WriteLine($"  Peer ID:          {localPeer.Identity.PeerId}");
        Console.WriteLine($"  Listen addresses: {localPeer.ListenAddresses.Count}");
        foreach (var addr in localPeer.ListenAddresses)
        {
            Console.WriteLine($"    {AppendPeerId(addr, localPeer.Identity.PeerId)}");
        }

        Console.WriteLine($"\n  Routing table:    {sharedState.PeerCount} peer(s)");

        var stats = kadProtocol.GetStatistics();
        foreach (var kvp in stats)
        {
            Console.WriteLine($"  {kvp.Key,-18} {kvp.Value}");
        }

        Console.WriteLine("  ════════════════════════════════════════════════");
    }

    //  CLI argument parsing

    private static CliConfig ParseArgs(string[] args)
    {
        var bootstrapAddresses = new List<string>();
        var listenAddresses = new List<string>();
        bool noRemoteBootstrap = false;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;

                case "--bootstrap" or "-b":
                    if (i + 1 < args.Length)
                        bootstrapAddresses.Add(args[++i]);
                    break;

                case "--listen" or "-l":
                    if (i + 1 < args.Length)
                        listenAddresses.Add(args[++i]);
                    break;

                case "--no-remote-bootstrap" or "--local-only":
                    noRemoteBootstrap = true;
                    break;

                case "--network" or "-n":
                    break;
            }
        }

        return new CliConfig(
            bootstrapAddresses,
            listenAddresses.Count > 0 ? listenAddresses : null,
            noRemoteBootstrap,
            showHelp);
    }

    //  Helpers

    private static IEnumerable<Multiaddress> BuildListenAddresses(List<string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            yield return Multiaddress.Decode("/ip4/0.0.0.0/tcp/0");
            yield return Multiaddress.Decode("/ip6/::/tcp/0");
            yield break;
        }

        foreach (var address in overrides)
        {
            yield return Multiaddress.Decode(StripPeerIdComponent(address));
        }
    }

    /// <summary>
    /// Discovers LAN/WAN addresses the node is reachable at.
    /// Expands 0.0.0.0 listen addresses into concrete interface IPs.
    /// </summary>
    private static List<string> GetAdvertisedAddresses(ILocalPeer localPeer)
    {
        var result = new List<string>();
        var peerId = localPeer.Identity.PeerId;

        foreach (var listenAddr in localPeer.ListenAddresses)
        {
            var addrStr = listenAddr.ToString();

            if (!addrStr.Contains("/ip4/0.0.0.0/") && !addrStr.Contains("/ip6/::/"))
            {
                if (!addrStr.Contains("/ip4/127.") && !addrStr.Contains("/ip6/::1/"))
                    result.Add($"{addrStr}/p2p/{peerId}");
                continue;
            }

            var parts = addrStr.Split('/');
            var tcpIdx = Array.IndexOf(parts, "tcp");
            if (tcpIdx < 0 || tcpIdx + 1 >= parts.Length) continue;
            var port = parts[tcpIdx + 1];

            var isIPv4 = addrStr.Contains("/ip4/");

            // Enumerate network interfaces
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (isIPv4 && ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        result.Add($"/ip4/{ua.Address}/tcp/{port}/p2p/{peerId}");
                    }
                    else if (!isIPv4 && ua.Address.AddressFamily == AddressFamily.InterNetworkV6
                             && !ua.Address.IsIPv6LinkLocal)
                    {
                        result.Add($"/ip6/{ua.Address}/tcp/{port}/p2p/{peerId}");
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns all non-loopback IPv4 addresses on the machine.
    /// </summary>
    private static List<IPAddress> GetLanIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(ua.Address);
            }
        }
        return result;
    }

    /// <summary>
    /// Extracts the TCP port from the first listen address.
    /// </summary>
    private static string GetListenPort(ILocalPeer localPeer)
    {
        foreach (var addr in localPeer.ListenAddresses)
        {
            var parts = addr.ToString().Split('/');
            var tcpIdx = Array.IndexOf(parts, "tcp");
            if (tcpIdx >= 0 && tcpIdx + 1 < parts.Length)
                return parts[tcpIdx + 1];
        }
        return "0";
    }

    private static string StripPeerIdComponent(string multiaddr)
    {
        var idx = multiaddr.IndexOf("/p2p/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? multiaddr[..idx] : multiaddr;
    }

    private static Multiaddress AppendPeerId(Multiaddress address, PeerId peerId)
    {
        var text = address.ToString();
        if (text.Contains("/p2p/", StringComparison.OrdinalIgnoreCase))
            return address;
        return Multiaddress.Decode($"{text}/p2p/{peerId}");
    }

    private static string TruncatePeerId(PeerId? peerId)
    {
        if (peerId is null) return "(null)";
        var s = peerId.ToString();
        return s.Length > 16 ? $"{s[..8]}...{s[^8..]}" : s;
    }

    private static void ShowUsage()
    {
        Console.WriteLine("KadDHT Demo -- Kademlia Distributed Hash Table over libp2p");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run                                          # All interfaces, random port");
        Console.WriteLine("  dotnet run -- --listen /ip4/0.0.0.0/tcp/40001       # All interfaces, fixed port");
        Console.WriteLine("  dotnet run -- --listen /ip4/127.0.0.1/tcp/40001     # Localhost only");
        Console.WriteLine("  dotnet run -- --bootstrap <multiaddr>               # Connect to peer");
        Console.WriteLine("  dotnet run -- --help                                # This help");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --listen, -l <addr>     Bind to a specific listen address");
        Console.WriteLine("  --bootstrap, -b <addr>  Connect to a bootstrap peer (repeatable)");
        Console.WriteLine("  --no-remote-bootstrap   Do not use default bootstrap nodes");
        Console.WriteLine("  --local-only            Alias for --no-remote-bootstrap");
        Console.WriteLine();
        Console.WriteLine("Same-machine testing (localhost only):");
        Console.WriteLine();
        Console.WriteLine("  # Terminal 1 -- listener:");
        Console.WriteLine("  dotnet run -- --listen /ip4/127.0.0.1/tcp/40001");
        Console.WriteLine();
        Console.WriteLine("  # Terminal 2 -- dialer (copy PeerID from listener output):");
        Console.WriteLine("  dotnet run -- --listen /ip4/127.0.0.1/tcp/40002 \\");
        Console.WriteLine("    --bootstrap /ip4/127.0.0.1/tcp/40001/p2p/<PeerID>");
        Console.WriteLine();
        Console.WriteLine("Cross-machine testing (LAN):");
        Console.WriteLine();
        Console.WriteLine("  # PC-A -- listener (bind all interfaces):");
        Console.WriteLine("  dotnet run -- --listen /ip4/0.0.0.0/tcp/40001");
        Console.WriteLine();
        Console.WriteLine("  # PC-B -- dialer (use PC-A's LAN IP from 'Reachable from' output):");
        Console.WriteLine("  dotnet run -- --listen /ip4/0.0.0.0/tcp/40001 \\");
        Console.WriteLine("    --bootstrap /ip4/192.168.x.x/tcp/40001/p2p/<PeerID>");
    }
}
