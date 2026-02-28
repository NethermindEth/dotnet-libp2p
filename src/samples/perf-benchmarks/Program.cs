using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DataTransferBenchmark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using StackExchange.Redis;

class Program
{
    static async Task Main(string[] args)
    {
        var services = new ServiceCollection()
            .AddLibp2p(builder =>
            {
                // TCP is enabled by default, also enable QUIC
                return builder
                    .WithQuic()  // Enable QUIC transport
                    .AddProtocol<PerfProtocol>();
            })
            .AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Information) // Show information and above
                .AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Warning; // Only warnings/errors to stderr
                }))
            .BuildServiceProvider();

        var peerFactory = services.GetRequiredService<IPeerFactory>();
        var logger = services.GetRequiredService<ILogger<Program>>();

        // Read configuration from environment variables (following WRITE_A_PERF_TEST.md)
        var isDialer = Environment.GetEnvironmentVariable("IS_DIALER") == "true";
        var redisAddr = Environment.GetEnvironmentVariable("REDIS_ADDR") ?? "redis:6379";
        var testKey = Environment.GetEnvironmentVariable("TEST_KEY") ?? "default";
        var transport = Environment.GetEnvironmentVariable("TRANSPORT") ?? "tcp";
        var secureChannel = Environment.GetEnvironmentVariable("SECURE_CHANNEL");
        var muxer = Environment.GetEnvironmentVariable("MUXER");

        logger.LogInformation("Configuration:");
        logger.LogInformation("  IS_DIALER: {IsDialer}", isDialer);
        logger.LogInformation("  REDIS_ADDR: {RedisAddr}", redisAddr);
        logger.LogInformation("  TEST_KEY: {TestKey}", testKey);
        logger.LogInformation("  TRANSPORT: {Transport}", transport);
        logger.LogInformation("  SECURE_CHANNEL: {Secure}", secureChannel ?? "none");
        logger.LogInformation("  MUXER: {Muxer}", muxer ?? "none");

        // Validate transport
        if (transport != "tcp" && transport != "quic" && transport != "quic-v1")
        {
            logger.LogError("Unsupported transport: {Transport}. Supported: tcp, quic, quic-v1", transport);
            Environment.Exit(1);
        }

        // Dialer-specific parameters
        ulong uploadBytes = 1073741824;  // Default 1GB
        ulong downloadBytes = 1073741824;
        int uploadIterations = 10;
        int downloadIterations = 10;
        int latencyIterations = 100;

        if (isDialer)
        {
            // Read test parameters from environment
            if (ulong.TryParse(Environment.GetEnvironmentVariable("UPLOAD_BYTES"), out var ub))
                uploadBytes = ub;
            if (ulong.TryParse(Environment.GetEnvironmentVariable("DOWNLOAD_BYTES"), out var db))
                downloadBytes = db;
            if (int.TryParse(Environment.GetEnvironmentVariable("UPLOAD_ITERATIONS"), out var ui))
                uploadIterations = ui;
            if (int.TryParse(Environment.GetEnvironmentVariable("DOWNLOAD_ITERATIONS"), out var di))
                downloadIterations = di;
            if (int.TryParse(Environment.GetEnvironmentVariable("LATENCY_ITERATIONS"), out var li))
                latencyIterations = li;

            logger.LogInformation("Test Parameters:");
            logger.LogInformation("  UPLOAD_BYTES: {Bytes}", uploadBytes);
            logger.LogInformation("  DOWNLOAD_BYTES: {Bytes}", downloadBytes);
            logger.LogInformation("  UPLOAD_ITERATIONS: {Iters}", uploadIterations);
            logger.LogInformation("  DOWNLOAD_ITERATIONS: {Iters}", downloadIterations);
            logger.LogInformation("  LATENCY_ITERATIONS: {Iters}", latencyIterations);
        }

        // Route to listener or dialer based on IS_DIALER
        if (isDialer)
        {
            await RunDialer(peerFactory, logger, redisAddr, transport, testKey, uploadBytes, downloadBytes,
                uploadIterations, downloadIterations, latencyIterations);
        }
        else
        {
            await RunListener(peerFactory, logger, redisAddr, transport, testKey);
        }
    }

    static async Task RunListener(IPeerFactory peerFactory, ILogger<Program> logger, string redisAddr, string transport, string testKey)
    {
        logger.LogInformation("Starting perf listener...");

        // Get listener IP from environment
        var listenerIp = Environment.GetEnvironmentVariable("LISTENER_IP");
        if (string.IsNullOrEmpty(listenerIp))
        {
            logger.LogError("LISTENER_IP environment variable not set");
            Environment.Exit(1);
        }
        logger.LogInformation("Listener IP: {ListenerIp}", listenerIp);

        // Connect to Redis
        var redis = await ConnectionMultiplexer.ConnectAsync(redisAddr);
        var db = redis.GetDatabase();
        logger.LogInformation("Connected to Redis at {RedisAddr}", redisAddr);

        // Create libp2p host
        var localPeer = peerFactory.Create();

        // Get peer ID for constructing multiaddr
        var peerId = localPeer.Identity.PeerId.ToString();
        logger.LogInformation("Peer ID: {PeerId}", peerId);

        // Construct listen multiaddr - bind to LISTENER_IP (usually 0.0.0.0)
        string listenMultiaddr;
        int listenPort = 4001;
        if (transport == "quic" || transport == "quic-v1")
        {
            listenMultiaddr = $"/ip4/{listenerIp}/udp/{listenPort}/quic-v1";
        }
        else
        {
            listenMultiaddr = $"/ip4/{listenerIp}/tcp/{listenPort}";
        }

        logger.LogInformation("Will listen on: {Multiaddr}", listenMultiaddr);

        // Parse multiaddr and start listening
        var listenAddr = Multiaddress.Decode(listenMultiaddr);
        await localPeer.StartListenAsync(new[] { listenAddr });
        logger.LogInformation("Listener started");

        // Resolve the actual container IP for publishing to Redis
        // When LISTENER_IP is 0.0.0.0, we need to find the real routable IP
        string publishIp = listenerIp;
        if (listenerIp == "0.0.0.0")
        {
            publishIp = GetContainerIp(logger);
            logger.LogInformation("Resolved container IP: {Ip}", publishIp);
        }

        // Construct the publish multiaddr with the real IP
        string publishAddrBase;
        if (transport == "quic" || transport == "quic-v1")
        {
            publishAddrBase = $"/ip4/{publishIp}/udp/{listenPort}/quic-v1";
        }
        else
        {
            publishAddrBase = $"/ip4/{publishIp}/tcp/{listenPort}";
        }

        var publishMultiaddr = $"{publishAddrBase}/p2p/{peerId}";
        var listenerAddrKey = $"{testKey}_listener_multiaddr";
        await db.StringSetAsync(listenerAddrKey, publishMultiaddr);
        logger.LogInformation("Published multiaddr to Redis: {Multiaddr} (key: {Key})", publishMultiaddr, listenerAddrKey);

        logger.LogInformation("Listener ready, waiting for connections...");

        // Keep running indefinitely
        await Task.Delay(Timeout.Infinite);
    }

    static async Task<string> WaitForListener(IDatabase db, ILogger<Program> logger, string testKey)
    {
        var listenerAddrKey = $"{testKey}_listener_multiaddr";
        logger.LogInformation("Waiting for listener multiaddr (key: {Key})...", listenerAddrKey);

        for (int i = 0; i < 30; i++)
        {
            var addr = await db.StringGetAsync(listenerAddrKey);
            if (!addr.IsNullOrEmpty)
            {
                return addr.ToString()!.Trim();
            }
            await Task.Delay(500);
        }

        throw new TimeoutException($"Timeout waiting for listener multiaddr (key: {listenerAddrKey})");
    }

    static async Task RunDialer(IPeerFactory peerFactory, ILogger<Program> logger, string redisAddr,
        string transport, string testKey, ulong uploadBytes, ulong downloadBytes,
        int uploadIterations, int downloadIterations, int latencyIterations)
    {
        logger.LogInformation("Starting perf dialer...");

        // Connect to Redis
        var redis = await ConnectionMultiplexer.ConnectAsync(redisAddr);
        var db = redis.GetDatabase();
        logger.LogInformation("Connected to Redis at {RedisAddr}", redisAddr);

        // Wait for listener multiaddr
        var listenerAddr = await WaitForListener(db, logger, testKey);
        if (string.IsNullOrWhiteSpace(listenerAddr))
        {
            logger.LogError("Listener multiaddr from Redis is empty");
            throw new InvalidOperationException("Listener multiaddr is empty");
        }
        logger.LogInformation("Got listener multiaddr: {Addr}", listenerAddr);

        // Give listener a moment to be fully ready
        await Task.Delay(500);

        // Create libp2p host
        var localPeer = peerFactory.Create();
        logger.LogInformation("Client peer created with ID: {PeerId}", localPeer.Identity.PeerId);

        // Initialize the peer by starting to listen on a local address
        // This ensures the peer is ready for outbound connections
        string dialerListenAddr;
        if (transport == "quic" || transport == "quic-v1")
        {
            dialerListenAddr = "/ip4/0.0.0.0/udp/0/quic-v1";
        }
        else
        {
            dialerListenAddr = "/ip4/0.0.0.0/tcp/0";
        }
        await localPeer.StartListenAsync(new[] { Multiaddress.Decode(dialerListenAddr) });
        logger.LogInformation("Dialer peer initialized, listening on {Addr}", dialerListenAddr);

        try
        {
            // Parse listener multiaddr 
            var targetAddr = Multiaddress.Decode(listenerAddr);

            // Validate the multiaddr was parsed correctly
            if (targetAddr == null)
            {
                throw new InvalidOperationException(
                    $"Failed to parse listener multiaddr: {listenerAddr}");
            }

            // Check that the multiaddress has protocols (not empty)
            var protocols = targetAddr.Protocols;
            if (protocols == null || !protocols.Any())
            {
                throw new InvalidOperationException(
                    $"Parsed multiaddr has no protocols. Raw string: '{listenerAddr}', Parsed: '{targetAddr}'");
            }

            logger.LogInformation("Parsed multiaddr protocols: {Protocols}", 
                string.Join(", ", protocols.Select(p => p.Name)));

            // Validate the multiaddr has a peer ID (required for dialing)
            if (!listenerAddr.Contains("/p2p/"))
            {
                throw new InvalidOperationException(
                    $"Listener multiaddr missing /p2p/<peerId> component: {listenerAddr}");
            }

            logger.LogInformation("Will dial protocol directly to: {Addr}", targetAddr);

            // Run three measurements sequentially
            var uploadStats = await RunMeasurement(localPeer, logger, targetAddr, uploadBytes, 0, uploadIterations, "upload");
            var downloadStats = await RunMeasurement(localPeer, logger, targetAddr, 0, downloadBytes, downloadIterations, "download");
            var latencyStats = await RunMeasurement(localPeer, logger, targetAddr, 1, 1, latencyIterations, "latency");

            logger.LogInformation("All measurements complete!");

            // Output results as YAML to stdout
            WriteStatsYaml("upload", uploadStats, uploadIterations, "Gbps", "F2");
            WriteStatsYaml("download", downloadStats, downloadIterations, "Gbps", "F2");
            WriteStatsYaml("latency", latencyStats, latencyIterations, "ms", "F3");

            logger.LogInformation("Results output complete");
        }
        catch (Exception ex)
        {
            logger.LogError("Dialer failed: {Message}", ex.Message);
            logger.LogError("Stack trace: {StackTrace}", ex.StackTrace);
            Environment.Exit(1);
        }
    }

    static void WriteStatsYaml(string name, Stats stats, int iterations, string unit, string fmt)
    {
        Console.WriteLine($"# {char.ToUpper(name[0])}{name[1..]} measurement");
        Console.WriteLine($"{name}:");
        Console.WriteLine($"  iterations: {iterations}");
        Console.WriteLine($"  min: {stats.Min.ToString(fmt)}");
        Console.WriteLine($"  q1: {stats.Q1.ToString(fmt)}");
        Console.WriteLine($"  median: {stats.Median.ToString(fmt)}");
        Console.WriteLine($"  q3: {stats.Q3.ToString(fmt)}");
        Console.WriteLine($"  max: {stats.Max.ToString(fmt)}");
        if (stats.Outliers.Count > 0)
        {
            var outliers = string.Join(", ", stats.Outliers.Select(v => v.ToString(fmt)));
                Console.WriteLine($"  outliers: [{outliers}]");
        }
        else
        {
            Console.WriteLine("  outliers: []");
        }
        if (stats.Samples.Count > 0)
        {
            var samples = string.Join(", ", stats.Samples.Select(v => v.ToString(fmt)));
            Console.WriteLine($"  samples: [{samples}]");
        }
        else
        {
            Console.WriteLine("  samples: []");
        }
        Console.WriteLine($"  unit: {unit}");
        Console.WriteLine();
    }

    /// <summary>
    /// Get the container's routable IP address (non-loopback IPv4).
    /// In Docker, this returns the IP assigned by the Docker network (e.g., 10.5.0.x).
    /// </summary>
    static string GetContainerIp(ILogger logger)
    {
        // Try to find a non-loopback IPv4 address from network interfaces
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up)
                continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    logger.LogInformation("Found routable IP {Ip} on interface {Iface}", addr.Address, iface.Name);
                    return addr.Address.ToString();
                }
            }
        }

        // Fallback: resolve hostname
        var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var addr in hostEntry.AddressList)
        {
            if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
            {
                return addr.ToString();
            }
        }

        throw new Exception("Could not determine container IP address");
    }

    // Box plot statistics
    class Stats
    {
        public double Min { get; set; }
        public double Q1 { get; set; }
        public double Median { get; set; }
        public double Q3 { get; set; }
        public double Max { get; set; }
        public List<double> Outliers { get; set; } = new();
        public List<double> Samples { get; set; } = new();
    }

    static Stats CalculateStats(List<double> values)
    {
        var samples = new List<double>(values); // Keep original samples
        values.Sort();

        var n = values.Count;

        // Calculate percentiles
        var q1 = Percentile(values, 25.0);
        var median = Percentile(values, 50.0);
        var q3 = Percentile(values, 75.0);

        // Calculate IQR and identify outliers
        var iqr = q3 - q1;
        var lowerFence = q1 - 1.5 * iqr;
        var upperFence = q3 + 1.5 * iqr;

        var outliers = values.Where(v => v < lowerFence || v > upperFence).ToList();
        var nonOutliers = values.Where(v => v >= lowerFence && v <= upperFence).ToList();

        // Min/Max from non-outliers only (matching Rust)
        double min, max;
        if (nonOutliers.Count > 0)
        {
            min = nonOutliers[0];
            max = nonOutliers[nonOutliers.Count - 1];
        }
        else
        {
            // Fallback if all values are outliers
            min = values[0];
            max = values[n - 1];
        }

        return new Stats
        {
            Min = min,
            Q1 = q1,
            Median = median,
            Q3 = q3,
            Max = max,
            Outliers = outliers,
            Samples = samples
        };
    }

    static double Percentile(List<double> sortedValues, double p)
    {
        var n = sortedValues.Count;
        var index = (p / 100.0) * (n - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = index - lower;
        return sortedValues[lower] * (1.0 - weight) + sortedValues[upper] * weight;
    }

    static async Task<Stats> RunMeasurement(ILocalPeer localPeer, ILogger<Program> logger, Multiaddress targetAddr,
        ulong uploadBytes, ulong downloadBytes, int iterations, string measurementType)
    {
        var values = new List<double>();

        logger.LogInformation("Running {Type} test ({Iterations} iterations)...", measurementType, iterations);

        // Establish a session (connection) to the remote peer once
        ISession? session = null;

        for (int i = 0; i < iterations; i++)
        {
            PerfProtocol.BytesToSend = uploadBytes;
            PerfProtocol.BytesToReceive = downloadBytes;

            var sw = Stopwatch.StartNew();

            try
            {
                // Establish session if not yet connected or if previous session was disconnected
                if (session == null)
                {
                    logger.LogInformation("Dialing target address (raw): '{Raw}', protocols: {Count}",
                        targetAddr.ToString(), targetAddr.Protocols?.Count() ?? 0);
                    
                    session = await localPeer.DialAsync(targetAddr);
                    logger.LogInformation("Session established to {Addr}", session.RemoteAddress);
                }

                // Dial the perf protocol on the session (opens a new stream)
                await session.DialAsync<PerfProtocol>();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Iteration {Iter} failed: {Message}\nFull Exception: {Ex}", 
                    i + 1, ex.Message, ex.ToString());
                // Reset session so we reconnect on next iteration
                try { if (session != null) await session.DisconnectAsync(); } catch { }
                session = null;
                continue; // Skip this iteration
            }

            sw.Stop();
            var elapsed = sw.Elapsed.TotalSeconds;

            // Calculate throughput (Gbps) or latency (seconds)
            double value;
            if (uploadBytes > 100 || downloadBytes > 100)
            {
                // Throughput test
                var bytes = Math.Max(uploadBytes, downloadBytes);
                value = (bytes * 8.0) / elapsed / 1_000_000_000.0;  // Gbps
            }
            else
            {
                // Latency test
                value = elapsed * 1000;  // Milliseconds
            }

            values.Add(value);
            logger.LogInformation("  Iteration {Iter}/{Total}: {Value}", i + 1, iterations,
                uploadBytes > 100 ? $"{value:F2} Gbps" : $"{value:F6} s");
        }

        if (values.Count == 0)
        {
            logger.LogError("All iterations failed for {Type} test", measurementType);
            throw new Exception($"All iterations failed for {measurementType} test");
        }

        return CalculateStats(values);
    }
}
