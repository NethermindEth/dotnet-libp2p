using System.Buffers;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;

namespace Libp2p.Core.Benchmarks;

[MemoryDiagnoser]
public class ChannelsBenchmark
{
    [Benchmark]
    public async Task Scenario1()
    {
        Channel.ReaderWriter channel = new();
        _ = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[3] { 1, 2, 3 }));
        ReadOnlySequence<byte> res3 = await channel.ReadAsync(1, ReadBlockingMode.WaitAll);
    }
}
