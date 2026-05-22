// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core.Tests;

public class ChannelTests
{
    [Test]
    public async Task Test_AsStream_ReadsDataWrittenToReverseChannel()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        byte[] data = [1, 2, 3, 4];

        Task write = stream.WriteAsync(data, 0, data.Length);

        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);
        await write;

        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(buffer, Is.EqualTo(data));
    }

    [Test]
    public void Test_AsStream_ReturnsCoreChannelStream()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        Assert.That(stream, Is.TypeOf<ChannelStream>());
        Assert.That(stream.GetType().Namespace, Is.EqualTo("Nethermind.Libp2p.Core"));
    }

    [Test]
    public async Task Test_AsStream_ReadsDataWrittenWithMemoryOverloads()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        byte[] data = [5, 6, 7, 8];

        ValueTask write = stream.WriteAsync(data.AsMemory());

        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer.AsMemory());
        await write;

        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(buffer, Is.EqualTo(data));
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthReadReturnsImmediately()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        byte[] buffer = new byte[4];
        int bytesRead = await stream.ReadAsync(buffer, 0, 0);

        Assert.That(bytesRead, Is.Zero);
        Assert.That(buffer, Is.EqualTo(new byte[4]));
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthReadReturnsAfterEnd()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        await channel.CloseAsync();
        byte[] buffer = new byte[4];
        int endedRead = await stream.ReadAsync(buffer, 0, 1);
        int secondRead = await stream.ReadAsync(buffer, 0, 1).WaitAsync(TimeSpan.FromSeconds(1));
        int secondMemoryRead = await stream.ReadAsync(buffer.AsMemory(0, 1)).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        int emptyRead = await stream.ReadAsync(buffer, 0, 0).WaitAsync(TimeSpan.FromSeconds(1));
        int emptyMemoryRead = await stream.ReadAsync(Memory<byte>.Empty).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(endedRead, Is.Zero);
            Assert.That(secondRead, Is.Zero);
            Assert.That(secondMemoryRead, Is.Zero);
            Assert.That(emptyRead, Is.Zero);
            Assert.That(emptyMemoryRead, Is.Zero);
            Assert.That(stream.CanRead, Is.False);
        });
    }

    [Test]
    public async Task Test_AsStream_SyncReadAfterEndReturnsImmediately()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        await channel.CloseAsync();
        byte[] buffer = new byte[1];
        int endedRead = stream.Read(buffer, 0, 1);
        int secondRead = await Task.Run(() => stream.Read(buffer, 0, 1)).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(endedRead, Is.Zero);
            Assert.That(secondRead, Is.Zero);
            Assert.That(stream.CanRead, Is.False);
        });
    }

    [Test]
    public async Task Test_AsStream_SyncReadDoesNotCaptureSynchronizationContext()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();
        byte[] buffer = new byte[1];

        Task<int> read = Task.Run(() =>
        {
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                return stream.Read(buffer, 0, buffer.Length);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        await Task.Delay(100);
        await reverseStream.WriteAsync([1], 0, 1).WaitAsync(TimeSpan.FromSeconds(1));
        int bytesRead = await read.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(bytesRead, Is.EqualTo(1));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1 }));
        });
    }

    [Test]
    public void Test_AsStream_ZeroLengthReadValidatesOffset()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        byte[] buffer = new byte[1];

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = stream.ReadAsync(buffer, 2, 0, CancellationToken.None));
    }

    [Test]
    public void Test_AsStream_ZeroLengthReadCancellationThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

#pragma warning disable CA2022 // Intentionally validate Stream.ReadAsync cancellation behavior.
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1], 0, 0, cts.Token));
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.ReadAsync(Memory<byte>.Empty, cts.Token));
#pragma warning restore CA2022
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthWriteCompletes()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        await stream.WriteAsync(Array.Empty<byte>(), 0, 0);

        Assert.That(stream.CanWrite, Is.True);
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthWriteDoesNotWaitForPendingWrite()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        ValueTask pendingWrite = stream.WriteAsync(new byte[] { 1 }.AsMemory());

        await stream.WriteAsync(Array.Empty<byte>(), 0, 0).WaitAsync(TimeSpan.FromSeconds(1));

        byte[] buffer = new byte[1];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);
        await pendingWrite;

        Assert.Multiple(() =>
        {
            Assert.That(bytesRead, Is.EqualTo(1));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1 }));
            Assert.That(stream.CanWrite, Is.True);
        });
    }

    [Test]
    public async Task Test_AsStream_SyncZeroLengthWriteDoesNotWaitForPendingWrite()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        ValueTask pendingWrite = stream.WriteAsync(new byte[] { 1 }.AsMemory());

        await Task.Run(() => stream.Write(Array.Empty<byte>(), 0, 0)).WaitAsync(TimeSpan.FromSeconds(1));

        byte[] buffer = new byte[1];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);
        await pendingWrite;

        Assert.Multiple(() =>
        {
            Assert.That(bytesRead, Is.EqualTo(1));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1 }));
            Assert.That(stream.CanWrite, Is.True);
        });
    }

    [Test]
    public async Task Test_AsStream_SyncWriteDoesNotCaptureSynchronizationContext()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        Task write = Task.Run(() =>
        {
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                stream.Write([1], 0, 1);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        await Task.Delay(100);
        byte[] buffer = new byte[1];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length).WaitAsync(TimeSpan.FromSeconds(1));
        await write.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(bytesRead, Is.EqualTo(1));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1 }));
        });
    }

    [Test]
    public void Test_AsStream_ReadAsyncCancellationThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

#pragma warning disable CA2022 // Intentionally validate Stream.ReadAsync cancellation behavior.
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1], 0, 1, cts.Token));
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1].AsMemory(), cts.Token));
#pragma warning restore CA2022
        Assert.That(stream.CanRead, Is.True);
    }

    [Test]
    public void Test_AsStream_WriteAsyncCancellationThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await stream.WriteAsync([1], 0, 1, cts.Token));
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[] { 1 }.AsMemory(), cts.Token));
        Assert.That(stream.CanWrite, Is.True);
    }

    [Test]
    public void Test_AsStream_ReadAsyncAfterDisposeThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        stream.Dispose();

#pragma warning disable CA2022 // Intentionally validate Stream.ReadAsync disposal behavior.
        Assert.CatchAsync<ObjectDisposedException>(async () => await stream.ReadAsync(new byte[1], 0, 0, CancellationToken.None));
        Assert.CatchAsync<ObjectDisposedException>(async () => await stream.ReadAsync(Memory<byte>.Empty, CancellationToken.None));
#pragma warning restore CA2022
        Assert.That(stream.CanRead, Is.False);
    }

    [Test]
    public void Test_AsStream_WriteAsyncAfterDisposeThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        stream.Dispose();

        Assert.CatchAsync<ObjectDisposedException>(async () => await stream.WriteAsync(new byte[1], 0, 0, CancellationToken.None));
        Assert.CatchAsync<ObjectDisposedException>(async () => await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        Assert.That(stream.CanWrite, Is.False);
    }

    [Test]
    public void Test_AsStream_NonSeekableMembersThrow()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        Assert.Multiple(() =>
        {
            Assert.That(stream.CanSeek, Is.False);
            Assert.Throws<NotSupportedException>(() => _ = stream.Length);
            Assert.Throws<NotSupportedException>(() => _ = stream.Position);
            Assert.Throws<NotSupportedException>(() => stream.Position = 1);
            Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        });
    }

    [Test]
    public void Test_AsStream_DisposedMembersThrow()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        stream.Dispose();

        Assert.Multiple(() =>
        {
            Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
            Assert.Throws<ObjectDisposedException>(() => _ = stream.Position);
            Assert.Throws<ObjectDisposedException>(() => stream.Position = 1);
            Assert.Throws<ObjectDisposedException>(() => stream.Flush());
            Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
            Assert.Throws<ObjectDisposedException>(() => stream.SetLength(1));
        });
    }

    [Test]
    public void Test_AsStream_CancelledAsyncAfterDisposeThrowsCancellation()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using CancellationTokenSource cts = new();
        stream.Dispose();
        cts.Cancel();

#pragma warning disable CA2022 // Intentionally validate Stream.ReadAsync cancellation behavior.
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1], 0, 0, cts.Token));
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.ReadAsync(Memory<byte>.Empty, cts.Token));
#pragma warning restore CA2022
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[1], 0, 0, cts.Token));
        Assert.CatchAsync<OperationCanceledException>(async () => await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, cts.Token));
    }

    [Test]
    public void Test_AsStream_NullChannelThrows()
    {
        IChannel? channel = null;

        Assert.Throws<ArgumentNullException>(() => channel!.AsStream());
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
