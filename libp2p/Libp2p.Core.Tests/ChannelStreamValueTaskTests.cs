// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace Nethermind.Libp2p.Core.Tests;

public class ChannelStreamValueTaskTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SynchronousReadWaitsForTheSourceAndConsumesItOnce(bool span, bool fail)
    {
        PendingChannel channel = new();
        using ChannelStream stream = new(channel, leaveOpen: true);
        byte[] target = [0xee, 0xee, 0xee, 0xee, 0xee];
        Task<int> reading = Task.Run(() => span ? stream.Read(target.AsSpan(1, 3)) : stream.Read(target, 1, 3));
        IOException failure = new("Read failed");
        try
        {
            await channel.Read.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(channel.Read.Consumptions, Is.Zero, "A pending source must be awaited before GetResult is called.");
            if (fail)
            {
                channel.Read.Fail(failure);
                Assert.That(Assert.ThrowsAsync<IOException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(10))), Is.SameAs(failure));
            }
            else
            {
                channel.Read.Complete(new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(new byte[] { 3, 7, 11 }) });
                Assert.That(await reading.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(3));
                Assert.That(target, Is.EqualTo(new byte[] { 0xee, 3, 7, 11, 0xee }));
            }
            Assert.That(channel.Read.Consumptions, Is.EqualTo(1));
        }
        finally
        {
            channel.Read.Complete(ReadResult.Ended);
            await reading.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SynchronousWriteWaitsForTheSourceAndConsumesItOnce(bool span, bool fail)
    {
        PendingChannel channel = new();
        using ChannelStream stream = new(channel, leaveOpen: true);
        byte[] source = [0xee, 3, 7, 11, 0xee];
        Task writing = Task.Run(() =>
        {
            if (span) stream.Write(source.AsSpan(1, 3));
            else stream.Write(source, 1, 3);
        });
        IOException failure = new("Write failed");
        try
        {
            await channel.Write.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(channel.Write.Consumptions, Is.Zero, "A pending source must be awaited before GetResult is called.");
            Assert.That(channel.Published.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11 }));
            if (fail)
            {
                channel.Write.Fail(failure);
                Assert.That(Assert.ThrowsAsync<IOException>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(10))), Is.SameAs(failure));
            }
            else
            {
                channel.Write.Complete(IOResult.Ok);
                await writing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.That(channel.Write.Consumptions, Is.EqualTo(1));
        }
        finally
        {
            channel.Write.Complete(IOResult.Aborted);
            await writing.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class PendingChannel : IChannel
    {
        public PendingSource<ReadResult> Read { get; } = new();
        public PendingSource<IOResult> Write { get; } = new();
        public ReadOnlySequence<byte> Published { get; private set; }

        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
            => Read.Task;

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            Published = bytes;
            return Write.Task;
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
            => throw new AssertionException("These stream operations must not publish EOF.");
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => System.Threading.Tasks.Task.CompletedTask.GetAwaiter();
    }

    private sealed class PendingSource<T> : IValueTaskSource<T>
    {
        private ManualResetValueTaskSourceCore<T> _source = new() { RunContinuationsAsynchronously = true };
        private int _completed;
        private int _consumptions;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Consumptions => Volatile.Read(ref _consumptions);
        public ValueTask<T> Task => new(this, _source.Version);

        public void Complete(T result)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _source.SetResult(result);
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _source.SetException(exception);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        public T GetResult(short token)
        {
            Interlocked.Increment(ref _consumptions);
            Waiting.TrySetResult();
            return _source.GetResult(token);
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _source.OnCompleted(continuation, state, token, flags);
            Waiting.TrySetResult();
        }
    }
}
