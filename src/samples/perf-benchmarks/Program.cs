using System.Diagnostics;
using System.Text.Json;
using DataTransferBenchmark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Multiformats.Address;

class Program
{
    static async Task Main(string[] args)
    {
        var services = new ServiceCollection()
            .AddLibp2p(builder => 
            {
                // Enable both TCP and QUIC transports
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

        string? serverAddress = null;
        string? multiaddr = null;
        bool runServer = false;
        ulong? uploadBytes = null;
        ulong? downloadBytes = null;
        string transport = "tcp";

        // Parse command line arguments like Go implementation
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--run-server")
            {
                runServer = true;
            }
            else if (args[i] == "--multiaddr" && i + 1 < args.Length)
            {
                multiaddr = args[++i];
            }
            else if (args[i] == "--server-address" && i + 1 < args.Length)
            {
                serverAddress = args[++i];
            }
            else if (args[i] == "--upload-bytes" && i + 1 < args.Length)
            {
                if (ulong.TryParse(args[++i], out ulong bytes))
                {
                    uploadBytes = bytes;
                }
            }
            else if (args[i] == "--download-bytes" && i + 1 < args.Length)
            {
                if (ulong.TryParse(args[++i], out ulong bytes))
                {
                    downloadBytes = bytes;
                }
            }
            else if (args[i] == "--transport" && i + 1 < args.Length)
            {
                transport = args[++i];
                // Validate transport - support both TCP and QUIC
                if (transport != "tcp" && transport != "quic")
                {
                    logger.LogError("Unsupported transport: {Transport}. Supported transports are 'tcp' and 'quic'", transport);
                    Environment.Exit(1);
                }
            }
        }

        // Both TCP and QUIC transports are now enabled via WithQuic() in the builder

        // Convert host:port to multiaddr format if multiaddr not directly provided
        if (multiaddr == null && serverAddress != null)
        {
            var parts = serverAddress.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int port))
            {
                if (runServer)
                {
                    multiaddr = transport == "quic" 
                        ? $"/ip4/{parts[0]}/udp/{port}/quic-v1"
                        : $"/ip4/{parts[0]}/tcp/{port}";
                }
                else
                {
                    // Generate deterministic peer ID like Go does for server
                    var serverIdentity = GenerateDeterministicIdentity(1);
                    var serverPeerId = serverIdentity.PeerId.ToString();
                    multiaddr = transport == "quic"
                        ? $"/ip4/{parts[0]}/udp/{port}/quic-v1/p2p/{serverPeerId}"
                        : $"/ip4/{parts[0]}/tcp/{port}/p2p/{serverPeerId}";
                }
            }
            else
            {
                logger.LogError("Invalid server address format. Use host:port");
                Environment.Exit(1);
            }
        }
        
        if (runServer)
        {
            // Server mode
            if (serverAddress == null)
            {
                logger.LogError("Server address must be specified with --server-address");
                Environment.Exit(1);
            }
            
            //var identity = new Identity(Enumerable.Repeat((byte)42, 32).ToArray());
            var identity = GenerateDeterministicIdentity(1);
            var localPeer = peerFactory.Create(identity);
            
            // Convert server address to multiaddr for listening
            var parts = serverAddress.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int port))
            {
                // Listen on the requested transport primarily (for debugging QUIC)
                var tcpAddr = Multiaddress.Decode($"/ip4/{parts[0]}/tcp/{port}");
                var quicAddr = Multiaddress.Decode($"/ip4/{parts[0]}/udp/{port}/quic-v1");
                
                // Always try to start with both TCP and QUIC transports when QUIC is enabled
                try
                {
                    // Try both transports
                    await localPeer.StartListenAsync(new[] { tcpAddr, quicAddr });
                    logger.LogCritical("Started listening on TCP and QUIC transports");
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to start QUIC transport: {Message}, falling back to TCP only", ex.Message);
                    // Fallback to TCP only if QUIC fails
                    await localPeer.StartListenAsync(new[] { tcpAddr });
                    logger.LogInformation("Started listening on TCP transport only");
                }
            }
            else
            {
                logger.LogError("Invalid server address format. Use host:port");
                Environment.Exit(1);
            }

            // Print listening addresses like Go implementation
            logger.LogCritical("Number of listening addresses: {Count}", localPeer.ListenAddresses.Count);
            foreach (var addr in localPeer.ListenAddresses)
            {
                logger.LogCritical("{Address}", addr);
            }
            
            // If no addresses, there might be an issue with QUIC binding
            if (localPeer.ListenAddresses.Count == 0)
            {
                logger.LogWarning("No listening addresses found! QUIC may not have bound correctly.");
                logger.LogInformation("Local peer created but no listening addresses available");
            }

            // Keep running until cancelled
            var tcs = new TaskCompletionSource<object>();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                tcs.SetResult(null!);
            };
            await tcs.Task;
        }
        else
        {
            // Client mode
            logger.LogInformation("Starting client mode");
            if (serverAddress == null)
            {
                logger.LogError("Server address must be specified with --server-address");
                Environment.Exit(1);
            }
            logger.LogInformation("Server address: {ServerAddress}", serverAddress);

            logger.LogInformation("Creating client identity");
            // var identity = new Identity(Enumerable.Repeat((byte)43, 32).ToArray()); // Different identity
            // var localPeer = peerFactory.Create(identity);
            
            // // Start client - listen on both transports
            // await localPeer.StartListenAsync(new[] { 
            //     Multiaddress.Decode("/ip4/127.0.0.1/tcp/0"),
            //     Multiaddress.Decode("/ip4/127.0.0.1/udp/0/quic-v1")
            // });

             var localPeer = peerFactory.Create();
             logger.LogInformation("Client peer created");

            try
            {
                // Set the upload/download sizes
                PerfProtocol.BytesToSend = uploadBytes;
                PerfProtocol.BytesToReceive = downloadBytes;
                logger.LogInformation("Set protocol parameters: upload={Upload}, download={Download}", uploadBytes, downloadBytes);
                
                // Reset counters for actual measurement
                PerfProtocol.ActualBytesSent = 0;
                PerfProtocol.ActualBytesReceived = 0;
                logger.LogInformation("Reset protocol counters");

                var startTime = DateTime.UtcNow;
                logger.LogInformation("Benchmark start time: {StartTime}", startTime);

                // Connect to the server using the properly formatted multiaddr
                if (multiaddr == null)
                {
                    logger.LogError("Error: Could not determine target multiaddr");
                    Environment.Exit(1);
                }
                logger.LogInformation("Target multiaddr: {Multiaddr}", multiaddr);
                
                var targetAddr = Multiaddress.Decode(multiaddr);
                logger.LogInformation("Dialing server at: {TargetAddr}", targetAddr);
                var remotePeer = await localPeer.DialAsync(targetAddr);
                logger.LogInformation("Successfully connected to server");
                
                // Run benchmark
                ulong actualUploadBytes = 0;
                ulong actualDownloadBytes = 0;
                
                try
                {
                    logger.LogInformation("Starting protocol dial for PerfProtocol");
                    var protocolTask = remotePeer.DialAsync<PerfProtocol>();
                    logger.LogInformation("Protocol dial initiated, awaiting completion");

                    // Await protocol task directly — allow long-running benchmarks
                    await protocolTask; // Get any exceptions
                    logger.LogInformation("Protocol execution completed successfully");
                    
                    // Get actual transfer amounts from the protocol instance
                    // Since the protocol execution may have partially succeeded
                    actualUploadBytes = PerfProtocol.ActualBytesSent;
                    actualDownloadBytes = PerfProtocol.ActualBytesReceived;
                    logger.LogInformation("Actual transfer: sent={Sent}, received={Received}", actualUploadBytes, actualDownloadBytes);
                }
                catch (Exception ex)
                {
                    logger.LogError("Protocol execution failed: {Message}", ex.Message);
                    logger.LogError("Stack trace: {StackTrace}", ex.StackTrace);
                    Environment.Exit(1);
                }
                finally
                {
                    try
                    {
                        logger.LogInformation("Disconnecting from server");
                        await remotePeer.DisconnectAsync();
                        logger.LogInformation("Disconnected successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Error during disconnect: {Message}", ex.Message);
                    }
                }

                // Output final result like Go implementation
                var elapsed = DateTime.UtcNow - startTime;
                logger.LogInformation("Benchmark completed in {Elapsed} seconds", elapsed.TotalSeconds);
                var result = new Result
                {
                    Type = "final",
                    TimeSeconds = Math.Round(elapsed.TotalSeconds, 3),
                    UploadBytes = actualUploadBytes,
                    DownloadBytes = actualDownloadBytes
                };

                var jsonOutput = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                logger.LogCritical("{JsonOutput}", jsonOutput);
            }
            catch (Exception ex)
            {
                logger.LogError("Error: {Message}", ex.Message);
                logger.LogError("Stack trace: {StackTrace}", ex.StackTrace);
                
                
                var result = new Result
                {
                    Type = "final",
                    TimeSeconds = 0.0,
                    UploadBytes = 0UL,
                    DownloadBytes = 0UL
                };

                var jsonOutput = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                logger.LogInformation("{JsonOutput}", jsonOutput);
                
                Environment.Exit(1);
            }
        }
    }
   // private static string GenerateDeterministicPeerId()
    private static Identity GenerateDeterministicIdentity(byte seed)
    {
        var fixedSeed = new byte[32];
        Array.Fill(fixedSeed, (byte)seed);//0);
        
        var identity = new Identity(fixedSeed);
       
        //return identity.PeerId.ToString();
         return new Identity(fixedSeed);

    }
}
