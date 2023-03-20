// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core.Tests;

public class ChannelsTests
{
    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested()
    {
        Channel.ReaderWriter channel = new ();
        _ = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[3] { 1, 2, 3 }));
        ReadOnlySequence<byte> res1 = await channel.ReadAsync(1, ReadBlockingMode.WaitAll);
        Assert.That(res1.ToArray().Length, Is.EqualTo(1));
        Assert.That(res1.ToArray()[0], Is.EqualTo(1));
        ReadOnlySequence<byte> res2 = await channel.ReadAsync(1, ReadBlockingMode.WaitAll);
        Assert.That(res2.ToArray().Length, Is.EqualTo(1));
        Assert.That(res2.ToArray()[0], Is.EqualTo(2));
        ReadOnlySequence<byte> res3 = await channel.ReadAsync(1, ReadBlockingMode.WaitAll);
//        Assert.That(isWritten, Is.True);
        Assert.That(res3.ToArray().Length, Is.EqualTo(1));
        Assert.That(res3.ToArray()[0], Is.EqualTo(3));
        await Task.Delay(2000).ContinueWith((t)=>channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 42 })));

        ReadOnlySequence<byte> res4 = await channel.ReadAsync(0, ReadBlockingMode.WaitAll);
    }
    
    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested2()
    {
        Channel.ReaderWriter channel = new Channel.ReaderWriter();
        _ = Task.Run(async () => await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 })));
        ReadOnlySequence<byte> res1 = await channel.ReadAsync(3, ReadBlockingMode.WaitAny);
        Assert.That(res1.ToArray().Length, Is.EqualTo(2));
    }
    
    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested3()
    {
        Channel.ReaderWriter channel = new Channel.ReaderWriter();
        ReadOnlySequence<byte> res1 = await channel.ReadAsync(3, ReadBlockingMode.DontWait);
        Assert.That(res1.ToArray().Length, Is.EqualTo(0));
    }
    
    
    [Test]
    public void Test_ChannelWrites_WhenReadIsRequested4()
    {
        var sequence = new ReadOnlySequence<byte>(new byte[] {1, 2, 3});
        var res = sequence.Prepend(new byte[] { 0 });
        var d = res.Length;
        Assert.That(res.ToArray(), Is.EquivalentTo(new byte[]{0,1,2,3}));
        Assert.That(res.ToArray()[0], Is.EqualTo(0));
        
        
        var res2 = res.Prepend(new byte[] { 42 });
        var d2 = res2.Length;
        Assert.That(res2.ToArray(), Is.EquivalentTo(new byte[]{42,0,1,2,3}));
        Assert.That(res2.ToArray()[0], Is.EqualTo(42));
    }
    
    [Test]
    public void Test_ChannelWrites_WhenReadIsRequested24()
    {
        var sequence = new ReadOnlySequence<byte>(new byte[] {1, 2, 3});
        var res = sequence.Append(new byte[] { 0 });
        var d = res.Length;
        Assert.That(res.ToArray(), Is.EquivalentTo(new byte[]{1,2,3, 0}));
        Assert.That(res.ToArray()[3], Is.EqualTo(0));
        
        
        var res2 = res.Append(new byte[] { 42 });
        var d2 = res2.Length;
        Assert.That(res2.ToArray(), Is.EquivalentTo(new byte[]{1,2,3, 0, 42}));
        Assert.That(res2.ToArray()[4], Is.EqualTo(42));
    }
    // [Test]
    // public async Task Test_ChannelWrites_WhenReadIsRequested()
    // {
    //     IChannel channel = new Channel();
    //     bool isWritten = false;
    //     
    //     
    //     Task.Run(async () =>
    //     {
    //         await channel.Writer.WriteAsync(new byte[3]);
    //         isWritten = true;
    //     });
    //     await channel.Reader.ReadAsync(1, ReadBlockingMode.WaitAll);
    //     Assert.That(isWritten, Is.False);
    //     await channel.Reader.ReadAsync(1, ReadBlockingMode.WaitAll);
    //     Assert.That(isWritten, Is.False);
    //     await channel.Reader.ReadAsync(1, ReadBlockingMode.WaitAll);
    //     Assert.That(isWritten, Is.True);
    // }
    // [Test]
    // public async Task Test_ChannelWriteBlocks_WhenReadIsNotRequested()
    // {
    //     
    // }
    // [Test]
    // public async Task Test_ChannelWritesPartByPart_WhenAPartIsRequested()
    // {
    //     IChannel channel = new Channel();
    //     Task.Run(() =>
    //     {
    //
    //     });
    //     await channel.Reader.ReadAsync(1, );
    // }
    // [Test]
    // public async Task Test_ChannelReadBlocks_WhenNoData()
    // {
    //     
    // }
    // [Test]
    // public async Task Test_BlockingChannelReadBlocks_WhenNotEnoughData()
    // {
    //     
    // }
    // [Test]
    // public async Task Test_nonBlockingChannelReadGetsMinimalAmount_WhenAtLeastOneByteAppears()
    // {
    //     
    // }
}
//
// internal class Channel : IChannel
// {
//     private IChannel _reversedChannel;
//
//     public Channel()
//     {
//         Id = "unknown";
//         Reader = new ReaderWriter();
//         Writer = new ReaderWriter();
//     }
//
//     private Channel(IReader reader, IWriter writer)
//     {
//         Id = "unknown";
//         Reader = reader;
//         Writer = writer;
//     }
//
//     private CancellationTokenSource State { get; init; } = new();
//
//     public IChannel Reverse
//     {
//         get
//         {
//             if (_reversedChannel != null)
//             {
//                 return _reversedChannel;
//             }
//
//             Channel x = new((ReaderWriter)Writer, (ReaderWriter)Reader)
//             {
//                 _reversedChannel = this,
//                 State = State,
//                 Id = Id + "-rev"
//             };
//             return _reversedChannel = x;
//         }
//     }
//
//     public string Id { get; set; }
//     public IReader Reader { get; private set; }
//     public IWriter Writer { get; private set; }
//
//     public bool IsClosed => State.IsCancellationRequested;
//
//     public CancellationToken Token => State.Token;
//
//     public async Task CloseAsync(bool graceful = true)
//     {
//         State.Cancel();
//     }
//
//     public void OnClose(Func<Task> action)
//     {
//         State.Token.Register(() => action().Wait());
//     }
//
//     public TaskAwaiter GetAwaiter()
//     {
//         return Task.Delay(-1, State.Token).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled).GetAwaiter();
//     }
//
//     public void Bind(IChannel parent)
//     {
//         Reader = (ReaderWriter)parent.Writer;
//         Writer = (ReaderWriter)parent.Reader;
//         OnClose(async () => { (parent as Channel).State.Cancel(); });
//         (parent as Channel).OnClose(async () => { State.Cancel(); });
//     }
//
//     internal class ReaderWriter : IReader, IWriter
//     {
//         private readonly byte[] iq = new byte[1024 * 1024];
//         private readonly SemaphoreSlim lk = new(0);
//         private int rPtr;
//         private int wPtr;
//         public string Name { get; set; }
//
//
//         private ArraySegment<byte> _bytes;
//         private ConcurrentQueue<ArraySegment<byte>> _fragements = new();
//         private ManualResetEventSlim _lock = new ManualResetEventSlim();
//
//         internal class MemorySegment<T> : ReadOnlySequenceSegment<T>
//         {
//             public MemorySegment(ReadOnlyMemory<T> memory)
//             {
//                 Memory = memory;
//             }
//
//             public MemorySegment<T> Append(ReadOnlyMemory<T> memory)
//             {
//                 var segment = new MemorySegment<T>(memory)
//                 {
//                     RunningIndex = RunningIndex + Memory.Length
//                 };
//
//                 Next = segment;
//
//                 return segment;
//             }
//         }
//
//         public async ValueTask<ReadOnlySequence<byte>> ReadAsync(int length,
//             ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
//         {
//             if (_bytes.Count == 0 && blockingMode == ReadBlockingMode.DontWait)
//             {
//                 return new ReadOnlySequence<byte>();
//             }
//             await _canRead.WaitAsync(token);
//
//             int bytesToRead = length != 0
//                 ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Count))
//                 : _bytes.Count;
//             
//             MemorySegment<byte>? chunk = null;
//             MemorySegment<byte>? lchunk;
//             do
//             {
//                 ReadOnlyMemory<byte> anotherChunk = null;
//                 
//                 if (_bytes.Count <= bytesToRead)
//                 {
//                     anotherChunk = _bytes;
//                     bytesToRead -= _bytes.Count;
//                     _bytes = null;
//                     _read.Set();
//                     _canWrite.Set();
//                 }
//                 else if (_bytes.Count > bytesToRead)
//                 {
//                     anotherChunk = _bytes[..bytesToRead];
//                     _bytes = _bytes[bytesToRead..];
//                     bytesToRead = 0;
//                 }
//
//                 if (chunk == null)
//                 {
//                     lchunk = chunk = new MemorySegment<byte>(anotherChunk);
//                 }
//                 else
//                 {
//                     lchunk = chunk.Append(anotherChunk);
//                 }
//             } while (bytesToRead != 0);
//
//             _canRead.Release();
//             return new ReadOnlySequence<byte>(chunk, 0, lchunk, lchunk.Memory.Length);
//         }
//
//         private ManualResetEventSlim _canWrite = new();
//         private ManualResetEventSlim _read = new();
//         private SemaphoreSlim _canRead = new(0, 1);
//
//         public ReaderWriter()
//         {
//             _canWrite.Set();
//             _read.Reset();
//         }
//
//         public async ValueTask WriteAsync(ArraySegment<byte> bytes)
//         {
//             if (bytes.Count == 0)
//             {
//                 return;
//             }
//
//             _canWrite.Wait();
//             if (_bytes != null)
//             {
//                 throw new InvalidProgramException();
//             }
//
//             _bytes = bytes;
//             _canRead.Release();
//             _read.Wait();
//         }
//     }
// }
//
// public enum ReadBlockingMode
// {
//     WaitAll,
//     WaitAny,
//     DontWait
// }
//
// public interface IReader
// {
//     ValueTask<ReadOnlySequence<byte>> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
//         CancellationToken token = default);
// }
//
// public interface IWriter
// {
//     ValueTask WriteAsync(ArraySegment<byte> bytes);
// }
//
// public interface IChannel
// {
//     IReader Reader { get; }
//     IWriter Writer { get; }
//     bool IsClosed { get; }
//     CancellationToken Token { get; }
//     Task CloseAsync(bool graceful = true);
//     void OnClose(Func<Task> action);
//     TaskAwaiter GetAwaiter();
// }
