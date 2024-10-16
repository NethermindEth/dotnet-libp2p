// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using DataTransferBenchmark;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public class PerfResult
{
    public string Type { get; set; }
    public double TimeSeconds { get; set; }
    public ulong UploadBytes { get; set; }
    public ulong DownloadBytes { get; set; }
}

public class PerfConfig
{
    public ulong BytesToSend { get; set; }
    public ulong BytesToRecv { get; set; }
}

class Program
{
    public static async Task Main(string[] args)
    {
        ulong uploadBytes = 1024L * 1024 * 100; // Example: 100 MiB
        ulong downloadBytes = 1024L * 1024 * 100; // Example: 100 MiB

        // Define PerfConfig with upload and download bytes
        PerfConfig perfConfig = new PerfConfig
        {
            BytesToSend = uploadBytes,
            BytesToRecv = downloadBytes
        };

        // Setting up the service provider with the PerfProtocol added.
        var serviceCollection = new ServiceCollection()
            .AddSingleton(perfConfig)
            .AddLibp2p(builder => builder.AddAppLayerProtocol<PerfProtocol>())
            .AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information)
                    .AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.TimestampFormat = "hh:mm:ss ";
                    });
            });

        var serviceProvider = serviceCollection.BuildServiceProvider();
        ILoggerFactory? _loggerFactory = serviceProvider?.GetService<ILoggerFactory>();
        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

        // Creating peers for communication
        ILocalPeer localPeer = peerFactory.Create();
        ILocalPeer remotePeer = peerFactory.Create();

        // Setting up listener and dialer
        IListener listener;
        try
        {
            listener = await localPeer.ListenAsync("/ip4/0.0.0.0/tcp/0");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to set up listener: {ex.Message}");
            return;
        }

        Multiaddress remoteAddr = listener.Address;
        Console.WriteLine($"Listener is listening on: {remoteAddr}");

        // Dial the Listener from the Remote Peer
        IRemotePeer connectedPeer;
        try
        {
            connectedPeer = await remotePeer.DialAsync(remoteAddr);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to dial the remote peer: {ex.Message}");
            return;
        }

        if (connectedPeer == null)
        {
            Console.Error.WriteLine("ConnectedPeer is null. Failed to establish connection.");
            return;
        }

        Console.WriteLine("Successfully connected to the remote peer.");

        // Start timing the performance test
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            // Initiate the PerfProtocol operation
            await connectedPeer.DialAsync<PerfProtocol>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during PerfProtocol DialAsync: {ex.Message}");
            return;
        }
        finally
        {
            stopwatch.Stop();
        }

        TimeSpan elapsedTime = stopwatch.Elapsed;

        // Stop the connection
        await connectedPeer.DisconnectAsync();

        // Create a performance result
        var result = new PerfResult
        {
            Type = "final",
            TimeSeconds = elapsedTime.TotalSeconds,
            UploadBytes = uploadBytes,
            DownloadBytes = downloadBytes
        };

        // Serialize the result to JSON
        string jsonResult = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(jsonResult);
    }
}
