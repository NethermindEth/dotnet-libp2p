using System;
using System.Buffers;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;
using Channel = Nethermind.Libp2p.Core.Channel;

namespace Libp2p.Core.Benchmarks;

[MemoryDiagnoser]
public class ChannelsBenchmark
{
    //class TestProtocol : IProtocol
    //{
    //    public string Id => "";

    //    public Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    //    {
    //        throw new System.NotImplementedException();
    //    }

    //    public Task ListenAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    //    {
    //        throw new System.NotImplementedException();
    //    }
    //}

    IChannel chan;
    IChannel revChan;

    [GlobalSetup]
    public void Setup()
    {
        chan = new Channel();
        revChan = ((Channel)chan).Reverse;
    }

    [Benchmark]
    public async Task Scenario1()
    {
        int PacketSize = 10 * 1024;
        int TotalSize = 1024 * 1024;

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < TotalSize; i += PacketSize)
            {
                byte[] array = new byte[PacketSize];
                await chan.WriteAsync(new ReadOnlySequence<byte>(array.AsMemory()));
            }
        });

        await Task.Run(async () =>
        {
            long i = 0;
            while (i < TotalSize)
            {
                i += (await revChan.ReadAsync(0, ReadBlockingMode.WaitAny)).Length;
            }
        });
    }
}
