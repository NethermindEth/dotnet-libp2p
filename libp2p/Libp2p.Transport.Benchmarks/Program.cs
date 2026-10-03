// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net.Quic;
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
        if (args is not [string outputPath, string stackName, string metricName])
        {
            throw new ArgumentException("Expected an output JSON path, stack, and metric");
        }

        (string Name, string Security, string ListenAddress) stack = Stacks.Single(x => x.Name == stackName);
        (string Name, ulong Upload, ulong Download) metric = Metrics.Single(x => x.Name == metricName);
        if (stack.Name is "quic-v1" && !QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is unavailable; install libmsquic before benchmarking");
        }
        Measurement result = await RunAsync(stack, metric);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"{result.Stack} {result.Metric}: {result.Median:F2} {result.Unit}");
    }

    private static async Task<Measurement> RunAsync(
        (string Name, string Security, string ListenAddress) stack,
        (string Name, ulong Upload, ulong Download) metric)
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
        await session.DialAsync<BenchmarkProtocol>(timeout.Token).WaitAsync(timeout.Token); // warmup
        long start = Stopwatch.GetTimestamp();
        await session.DialAsync<BenchmarkProtocol>(timeout.Token).WaitAsync(timeout.Token);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        await session.DisconnectAsync().WaitAsync(timeout.Token);

        double value = metric.Name is StreamOpenExchangeMetric ? seconds * 1000 : TransferBytes / (1024d * 1024) / seconds;
        return new(stack.Name, metric.Name, metric.Name is StreamOpenExchangeMetric ? "ms" : "MiB/s", value, [value]);
    }

    private sealed record Measurement(string Stack, string Metric, string Unit, double Median, double[] Samples);
}
