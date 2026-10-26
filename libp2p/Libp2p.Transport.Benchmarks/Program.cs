// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Net.Quic;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Transport.Benchmarks;

internal static class Program
{
    private const ulong TransferBytes = 32 * 1024;
    private const string StreamOpenExchangeMetric = "stream-open-exchange";

    private static readonly (string Name, string Security, string ListenAddress)[] Stacks =
    [
        ("tcp-noise-yamux", "noise", "/ip4/127.0.0.1/tcp/0"),
        ("tcp-tls-yamux", "tls", "/ip4/127.0.0.1/tcp/0"),
        ("quic-v1", "noise", "/ip4/127.0.0.1/udp/0/quic-v1")
    ];

    private static readonly (string Name, ulong Upload, ulong Download)[] Metrics =
    [
        ("upload", TransferBytes, 0),
        ("download", 0, TransferBytes),
        (StreamOpenExchangeMetric, 1, 1)
    ];

    private static async Task Main(string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            throw new ArgumentException("Expected an output JSON path, stack, metric, and optional sample count");
        }
        string outputPath = args[0];
        string stackName = args[1];
        string metricName = args[2];
        int sampleCount = args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        (string Name, string Security, string ListenAddress) stack = Stacks.Single(x => x.Name == stackName);
        (string Name, ulong Upload, ulong Download) metric = Metrics.Single(x => x.Name == metricName);
        if (stack.Name is "quic-v1" && !QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is unavailable; install libmsquic before benchmarking");
        }
        Measurement result = await RunAsync(stack, metric, sampleCount);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"{result.Stack} {result.Metric}: {result.Median:F2} {result.Unit}");
    }

    private static async Task<Measurement> RunAsync(
        (string Name, string Security, string ListenAddress) stack,
        (string Name, ulong Upload, ulong Download) metric,
        int sampleCount)
    {
        Environment.SetEnvironmentVariable("BENCHMARK_SECURITY", stack.Security);
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p<BenchmarkPeerFactoryBuilder>(builder => builder.AddProtocol<BenchmarkProtocol>())
            .AddLogging()
            .BuildServiceProvider();

        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer listener = factory.Create();
        await using ILocalPeer dialer = factory.Create();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));

        Multiaddress address = Multiaddress.Decode(stack.ListenAddress);
        await listener.StartListenAsync([address], timeout.Token);
        await dialer.StartListenAsync([Multiaddress.Decode(stack.ListenAddress)], timeout.Token);
        Multiaddress target = listener.ListenAddresses.Single();

        BenchmarkProtocol.BytesToSend = metric.Upload;
        BenchmarkProtocol.BytesToReceive = metric.Download;
        ISession session = await dialer.DialAsync(target, timeout.Token);
        int warmupCount = sampleCount == 1 ? 1 : 5;
        for (int i = 0; i < warmupCount; i++)
        {
            await session.DialAsync<BenchmarkProtocol>(timeout.Token).WaitAsync(timeout.Token);
        }
        double[] samples = new double[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await session.DialAsync<BenchmarkProtocol>(timeout.Token).WaitAsync(timeout.Token);
            double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            samples[i] = metric.Name is StreamOpenExchangeMetric ? seconds * 1000 : TransferBytes / (1024d * 1024) / seconds;
        }
        await session.DisconnectAsync().WaitAsync(timeout.Token);

        double[] sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        double median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        return new(stack.Name, metric.Name, metric.Name is StreamOpenExchangeMetric ? "ms" : "MiB/s", median, samples,
            metric.Upload, metric.Download, warmupCount, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            GCSettings.IsServerGC, Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), DateTimeOffset.UtcNow);
    }

    private sealed record Measurement(string Stack, string Metric, string Unit, double Median, double[] Samples,
        ulong UploadBytes, ulong DownloadBytes, int WarmupCount, string Runtime, string OS, string Architecture,
        int ProcessorCount, bool ServerGC, string? TieredCompilation, DateTimeOffset TimestampUtc);
}
