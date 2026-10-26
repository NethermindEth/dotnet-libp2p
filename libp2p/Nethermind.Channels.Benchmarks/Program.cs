// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

extern alias Baseline;

using System.Buffers;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Legacy = Baseline::Nethermind.Libp2p.Core;
#if !BASELINE_ONLY
using Owned = Nethermind.Channels;
#endif

namespace Nethermind.Channels.Benchmarks;

internal static class Program
{
    private const string MainRevision = "cf72a0c9e9420ff7b74d28e8b9feef1a91a8cc05";
    private const string ManagedRevision = "70b6c7321a2471dca985c6d15e8de30d0721c56b";
    private const int BufferSize = 64 * 1024;

    private static async Task Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 5)
            throw new ArgumentException("Usage: <variant> <scenario> [source revision] [allocation|throughput] [benchmark revision]");

        string variant = args[0];
        string scenario = args[1];
        string measurement = args.Length >= 4 ? args[3] : "allocation";
        if (measurement is not ("allocation" or "throughput")) throw new ArgumentException("Unknown measurement.");
        bool throughput = measurement == "throughput";
        (int length, int fragmentSize, int readSize, int operations) = scenario switch
        {
            "full-block" => (BufferSize, BufferSize, BufferSize, 128),
            "fragmented-waitall" => (4096, 1, 4096, 1),
            "small-reads" => (BufferSize, 64, 64, 4),
            "steady-small-reads" => (16 * BufferSize, 64, 64, 1),
            "forwarding" => (BufferSize, BufferSize, BufferSize, 128),
            _ => throw new ArgumentException("Unknown scenario.")
        };
#if CURRENT_CORE
        if (variant is not ("owned" or "core-owned" or "core-legacy"))
            throw new ArgumentException("This build references current Core and cannot measure historical variants.");
#else
        if (variant is not ("main-listener" or "main-dialer" or "managed" or "owned"))
            throw new ArgumentException("Unknown variant.");
#endif
#if BASELINE_ONLY
        if (variant == "owned") throw new ArgumentException("This build excludes owned benchmarks.");
#endif

        byte[] expected = new byte[length];
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 73 + 19) % 251);
        Func<Task> operation;
#if CURRENT_CORE
        // Observe indefinite legacy-result lifetime without allocating measurement scaffolding
        // per operation. Owned consumers instead release each result after checking its bytes.
        ReadOnlySequence<byte>[] retained = variant == "core-legacy" ? new ReadOnlySequence<byte>[length / readSize] : [];
#endif
#if !BASELINE_ONLY
        if (variant == "owned") operation = () => RunOwnedAsync(scenario == "forwarding", expected, fragmentSize, readSize,
            scenario == "steady-small-reads");
        else
#endif
#if CURRENT_CORE
            operation = () => RunCoreAsync(variant == "core-owned", scenario == "forwarding", expected, fragmentSize, readSize, retained,
                scenario == "steady-small-reads");
#else
            operation = () => RunLegacyAsync(variant, scenario == "forwarding", expected, fragmentSize, readSize,
                scenario == "steady-small-reads");
#endif

        // The counter is process-wide: asynchronous work may resume on any thread.
        // No output is produced until every measured operation and its tasks have completed.
        if (throughput) await RunForDurationAsync(operation, TimeSpan.FromSeconds(1));
        else for (int i = 0; i < 3; i++) await RunManyAsync(operation, operations);
        const int sampleCount = 5;
        long[] allocatedBytes = new long[sampleCount];
        double[] elapsedMilliseconds = new double[sampleCount];
        int[] operationCounts = new int[sampleCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            if (!throughput)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            if (throughput) operationCounts[sample] = await RunForDurationAsync(operation, TimeSpan.FromMilliseconds(500));
            else
            {
                await RunManyAsync(operation, operations);
                operationCounts[sample] = operations;
            }
            elapsedMilliseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocatedBytes[sample] = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        double[] bytesPerOperation = allocatedBytes.Select((value, i) => (double)value / operationCounts[i]).Order().ToArray();
        double[] operationsPerSecond = elapsedMilliseconds.Select((value, i) => operationCounts[i] * 1000 / value).ToArray();
        double[] sortedRates = operationsPerSecond.Order().ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Variant = variant,
            Scenario = scenario,
            Measurement = measurement,
            Revision = variant switch
            {
                "managed" => ManagedRevision,
                "owned" or "core-owned" or "core-legacy" => args.Length >= 3 ? args[2] : "unspecified",
                _ => MainRevision,
            },
            WorkingRevision = args.Length >= 3 ? args[2] : "unspecified",
            BenchmarkRevision = args.Length >= 5 ? args[4] : "unspecified",
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            ServerGC = GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            TimestampUtc = DateTimeOffset.UtcNow,
            PayloadBytes = length,
            FragmentBytes = fragmentSize,
            ReadBytes = readSize,
            BufferBytes = BufferSize,
            Cancellable = scenario == "steady-small-reads",
            OperationsPerSample = throughput ? (int?)null : operations,
            OperationCounts = operationCounts,
            WarmupSamples = throughput ? (int?)null : 3,
            MinimumWarmupMilliseconds = throughput ? (int?)1000 : null,
            MinimumSampleMilliseconds = throughput ? (int?)500 : null,
            AllocatedBytes = allocatedBytes,
            MedianBytesPerOperation = bytesPerOperation[sampleCount / 2],
            MinimumBytesPerOperation = bytesPerOperation[0],
            MaximumBytesPerOperation = bytesPerOperation[^1],
            ElapsedMilliseconds = elapsedMilliseconds,
            OperationsPerSecond = throughput ? operationsPerSecond : null,
            MedianOperationsPerSecond = throughput ? (double?)sortedRates[sampleCount / 2] : null,
            MinimumOperationsPerSecond = throughput ? (double?)sortedRates[0] : null,
            MaximumOperationsPerSecond = throughput ? (double?)sortedRates[^1] : null,
        }));
    }

    private static async Task RunManyAsync(Func<Task> operation, int count)
    {
        for (int i = 0; i < count; i++) await operation();
    }

    private static async Task<int> RunForDurationAsync(Func<Task> operation, TimeSpan duration)
    {
        int count = 0;
        long started = Stopwatch.GetTimestamp();
        do
        {
            await operation();
            count++;
        } while (Stopwatch.GetElapsedTime(started) < duration);
        return count;
    }

    private static async Task RunLegacyAsync(string variant, bool forwarding, byte[] expected, int fragmentSize, int readSize,
        bool cancellable)
    {
        using CancellationTokenSource? cancellation = cancellable ? new() : null;
        CancellationToken token = cancellation?.Token ?? default;
        Legacy.Channel source = new();
        Legacy.Channel? forwarded = forwarding ? new() : null;
        SyntheticSource input = new(expected, fragmentSize);
        Task consume = ConsumeLegacyAsync(forwarded?.Reverse ?? source.Reverse, expected, readSize, token);
        Task forward = forwarded is null ? Task.CompletedTask : ForwardLegacyAsync(source.Reverse, forwarded, expected.Length);
        try
        {
            Legacy.IOResult written = variant == "managed"
                ? await ((Legacy.IWriter)source).WriteFromAsync(input.ReadAsync, BufferSize, token)
                : await WriteLegacyReceiveLoopAsync(source, input, variant == "main-dialer", token);
            if (written != Legacy.IOResult.Ok) throw new InvalidOperationException($"Write failed: {written}");
            await consume;
            await forward;
        }
        finally
        {
            await source.CloseAsync();
            if (forwarded is not null) await forwarded.CloseAsync();
            await Task.WhenAll(consume, forward);
        }
    }

    private static async Task<Legacy.IOResult> WriteLegacyReceiveLoopAsync(Legacy.IWriter writer, SyntheticSource input, bool dialer,
        CancellationToken token)
    {
        // Mirrors main's TCP receive loops, including its final receive that reports EOF.
        for (; ; )
        {
            byte[] buffer = new byte[BufferSize];
            int count = await input.ReadAsync(buffer, token);
            if (count == 0) return Legacy.IOResult.Ok;
            ReadOnlySequence<byte> bytes = dialer
                ? new ReadOnlySequence<byte>(buffer[..count])
                : new ReadOnlySequence<byte>(buffer.AsMemory(0, count));
            Legacy.IOResult result = await writer.WriteAsync(bytes, token);
            if (result != Legacy.IOResult.Ok) return result;
        }
    }

    private static async Task ConsumeLegacyAsync(Legacy.IReader reader, byte[] expected, int readSize, CancellationToken token)
    {
        for (int offset = 0; offset < expected.Length; offset += readSize)
        {
            Legacy.ReadResult read = await reader.ReadAsync(readSize, token: token);
            if (read.Result != Legacy.IOResult.Ok) throw new InvalidOperationException($"Read failed: {read.Result}");
            Verify(read.Data, expected.AsSpan(offset, readSize));
        }
    }

    private static async Task ForwardLegacyAsync(Legacy.IReader reader, Legacy.IWriter writer, int length)
    {
        Legacy.ReadResult read = await reader.ReadAsync(length);
        if (read.Result != Legacy.IOResult.Ok) throw new InvalidOperationException($"Forward read failed: {read.Result}");
        if (await writer.WriteAsync(read.Data) != Legacy.IOResult.Ok) throw new InvalidOperationException("Forward write failed.");
    }

    private static void Verify(ReadOnlySequence<byte> bytes, ReadOnlySpan<byte> expected)
    {
        if (bytes.Length != expected.Length) throw new InvalidOperationException("Unexpected payload length.");
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            if (!segment.Span.SequenceEqual(expected[..segment.Length])) throw new InvalidOperationException("Corrupted payload.");
            expected = expected[segment.Length..];
        }
    }

#if !BASELINE_ONLY
    private static async Task RunOwnedAsync(bool forwarding, byte[] expected, int fragmentSize, int readSize, bool cancellable)
    {
        using CancellationTokenSource? cancellation = cancellable ? new() : null;
        CancellationToken token = cancellation?.Token ?? default;
        Owned.Channel source = new();
        Owned.Channel? forwarded = forwarding ? new() : null;
        SyntheticSource input = new(expected, fragmentSize);
        Task consume = ConsumeOwnedAsync(forwarded?.Reverse ?? source.Reverse, expected, readSize, token);
        Task forward = forwarded is null ? Task.CompletedTask : ForwardOwnedAsync(source.Reverse, forwarded, expected.Length);
        try
        {
            Owned.IOResult written = await ((Owned.IWriter)source).WriteFromAsync(input.ReadAsync, BufferSize, token);
            if (written != Owned.IOResult.Ok) throw new InvalidOperationException($"Write failed: {written}");
            await consume;
            await forward;
        }
        finally
        {
            await source.CloseAsync();
            if (forwarded is not null) await forwarded.CloseAsync();
            await Task.WhenAll(consume, forward);
        }
    }

    private static async Task ConsumeOwnedAsync(Owned.IReader reader, byte[] expected, int readSize, CancellationToken token)
    {
        for (int offset = 0; offset < expected.Length; offset += readSize)
        {
            using Owned.ReadResult read = await reader.ReadAsync(readSize, token: token);
            if (read.Result != Owned.IOResult.Ok) throw new InvalidOperationException($"Read failed: {read.Result}");
            Verify(read.Sequence, expected.AsSpan(offset, readSize));
        }
    }

    private static async Task ForwardOwnedAsync(Owned.IReader reader, Owned.IWriter writer, int length)
    {
        using Owned.ReadResult read = await reader.ReadAsync(length);
        if (read.Result != Owned.IOResult.Ok) throw new InvalidOperationException($"Forward read failed: {read.Result}");
        using Owned.PooledBuffer.Slice slice = read.ToSlice();
        if (await writer.WriteAsync(slice) != Owned.IOResult.Ok) throw new InvalidOperationException("Forward write failed.");
    }
#endif

#if CURRENT_CORE
    private static async Task RunCoreAsync(bool owned, bool forwarding, byte[] expected, int fragmentSize, int readSize,
        ReadOnlySequence<byte>[] retained, bool cancellable)
    {
        using CancellationTokenSource? cancellation = cancellable ? new() : null;
        CancellationToken token = cancellation?.Token ?? default;
        Legacy.Channel source = new();
        Legacy.Channel? forwarded = forwarding ? new() : null;
        SyntheticSource input = new(expected, fragmentSize);
        Task consume = ConsumeCoreAsync(forwarded?.Reverse ?? source.Reverse, expected, readSize, owned, retained, token);
        Task forward = forwarded is null ? Task.CompletedTask : ForwardCoreAsync(source.Reverse, forwarded, expected.Length, owned);
        try
        {
            Legacy.IOResult written = await ((Legacy.IWriter)source).WriteFromAsync(input.ReadAsync, BufferSize, token);
            if (written != Legacy.IOResult.Ok) throw new InvalidOperationException($"Write failed: {written}");
            await consume;
            await forward;
        }
        finally
        {
            await source.CloseAsync();
            if (forwarded is not null) await forwarded.CloseAsync();
            await Task.WhenAll(consume, forward);
        }

        // All receives, forwarding and teardown have finished before testing retained views.
        for (int i = 0; i < retained.Length; i++) Verify(retained[i], expected.AsSpan(i * readSize, readSize));
        Array.Clear(retained);
    }

    private static async Task ConsumeCoreAsync(Legacy.IReader reader, byte[] expected, int readSize, bool owned,
        ReadOnlySequence<byte>[] retained, CancellationToken token)
    {
        for (int offset = 0; offset < expected.Length; offset += readSize)
        {
            using Legacy.ReadResult read = owned ? await reader.ReadOwnedAsync(readSize, token: token) : await reader.ReadAsync(readSize, token: token);
            if (read.Result != Legacy.IOResult.Ok) throw new InvalidOperationException($"Read failed: {read.Result}");
            if (read.IsOwned != owned) throw new InvalidOperationException("The read did not use the expected ownership contract.");
            Verify(read.Data, expected.AsSpan(offset, readSize));
            if (!owned) retained[offset / readSize] = read.Data;
        }
    }

    private static async Task ForwardCoreAsync(Legacy.IReader reader, Legacy.IWriter writer, int length, bool owned)
    {
        using Legacy.ReadResult read = owned ? await reader.ReadOwnedAsync(length) : await reader.ReadAsync(length);
        if (read.Result != Legacy.IOResult.Ok) throw new InvalidOperationException($"Forward read failed: {read.Result}");
        Legacy.IOResult result;
        if (owned)
        {
            using Owned.PooledBuffer.Slice slice = read.ToSlice();
            result = await writer.WriteAsync(slice);
        }
        else result = await writer.WriteAsync(read.Data);
        if (result != Legacy.IOResult.Ok) throw new InvalidOperationException("Forward write failed.");
    }
#endif

    private sealed class SyntheticSource(byte[] payload, int fragmentSize)
    {
        private int _offset;

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(fragmentSize, destination.Length), payload.Length - _offset);
            payload.AsMemory(_offset, count).CopyTo(destination);
            _offset += count;
            return ValueTask.FromResult(count);
        }
    }
}
