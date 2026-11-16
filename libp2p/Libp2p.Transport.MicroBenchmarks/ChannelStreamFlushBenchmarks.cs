// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Transport.MicroBenchmarks;

[MemoryDiagnoser]
public class ChannelStreamFlushBenchmarks
{
    private const int Flushes = 64;

    private ChannelStream _stream = null!;

    [GlobalSetup]
    public void Setup() => _stream = new ChannelStream(new Channel());

    [GlobalCleanup]
    public void Cleanup() => _stream.Dispose();

    [Benchmark(OperationsPerInvoke = Flushes)]
    public async Task FlushAsync()
    {
        for (int i = 0; i < Flushes; i++)
        {
            await _stream.FlushAsync();
        }
    }
}
