// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Nethermind.Channels.Tests;

public class PooledBufferTests
{
    [TestCase(0)]
    [TestCase(8)]
    public void LogicalLengthDoesNotChangeThePooledArray(int length)
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(length, pool);
        Memory<byte> memory = buffer;
        ReadOnlyMemory<byte> readOnlyMemory = buffer;
        Assert.That(buffer.Length, Is.EqualTo(length));
        Assert.That(memory.Length, Is.EqualTo(length));
        Assert.That(readOnlyMemory.Length, Is.EqualTo(length));
        Assert.That(buffer.Span.Length, Is.EqualTo(length));
        Assert.That(buffer.ReadOnlySpan.Length, Is.EqualTo(length));
        Assert.That(MemoryMarshal.TryGetArray(readOnlyMemory, out ArraySegment<byte> segment), Is.True);
        Assert.That(segment.Array, Is.SameAs(pool.Rented));
        Assert.That(pool.Rented, Has.Length.EqualTo(length + TrackingPool.ExtraCapacity));
        buffer.Dispose();
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
        Assert.That(pool.ReturnedLength, Is.EqualTo(length + TrackingPool.ExtraCapacity));
        Assert.That(pool.Rented, Has.Length.EqualTo(length + TrackingPool.ExtraCapacity));
    }

    [Test]
    public void RepeatedOwnerDisposalLeavesItsSliceAlive()
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        byte[] expected = [3, 7, 11, 19];
        expected.CopyTo(buffer.Memory[2..]);
        PooledBuffer.Slice slice = buffer[2..6];
        PooledBuffer alias = buffer;
        buffer.Dispose();
        alias.Dispose();
        buffer.Dispose();

        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.That(pool.ReturnCount, Is.Zero);
        Assert.That(slice.Memory.ToArray(), Is.EqualTo(expected));
        Assert.Throws<ObjectDisposedException>(() => { _ = buffer.Memory; });
        Assert.Throws<ObjectDisposedException>(() => { _ = buffer[0..1]; });

        slice.Dispose();
        Assert.That(buffer.RefCount, Is.Zero);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    [Test]
    public void SliceAliasesReleaseOnlyTheirSharedLease()
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        PooledBuffer.Slice first = buffer[1..4];
        PooledBuffer.Slice alias = first;
        PooledBuffer.Slice sibling = buffer[4..7];
        sibling.Span.Fill(42);
        buffer.Dispose();
        first.Dispose();
        alias.Dispose();

        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.That(pool.ReturnCount, Is.Zero);
        Assert.That(sibling.Memory.ToArray(), Is.EqualTo(new byte[] { 42, 42, 42 }));
        Assert.Throws<ObjectDisposedException>(() => { _ = alias.Memory; });
        Assert.Throws<ObjectDisposedException>(() => { _ = alias[0..1]; });
        sibling.Dispose();
        sibling.Dispose();
        Assert.That(buffer.RefCount, Is.Zero);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    [Test]
    public void NestedSliceCanOutliveAndRetainAfterItsParentAndRoot()
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        for (int i = 0; i < buffer.Length; i++) buffer.Span[i] = (byte)(i * 7);
        PooledBuffer.Slice parent = buffer[2..7];
        PooledBuffer.Slice child = parent[1..^1];
        buffer.Dispose();
        parent.Dispose();
        Assert.That(child.Memory.ToArray(), Is.EqualTo(new byte[] { 21, 28, 35 }));

        PooledBuffer.Slice retained = child[1..];
        child.Dispose();
        Assert.That(retained.Memory.ToArray(), Is.EqualTo(new byte[] { 28, 35 }));
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.That(pool.ReturnCount, Is.Zero);
        retained.Dispose();
        Assert.That(buffer.RefCount, Is.Zero);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    [Test]
    public void EmptySliceAtTheEndStillOwnsALease()
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        PooledBuffer.Slice empty = buffer[8..8];
        buffer.Dispose();
        Assert.That(empty.Length, Is.Zero);
        Assert.That(empty.Memory.IsEmpty, Is.True);
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.That(pool.ReturnCount, Is.Zero);
        empty.Dispose();
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    [TestCase(-1, 1)]
    [TestCase(9, 0)]
    [TestCase(0, -1)]
    [TestCase(7, 2)]
    [TestCase(1, int.MaxValue)]
    public void InvalidOwnerSliceDoesNotAcquireALink(int offset, int length)
    {
        TrackingPool pool = new();
        using PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.LeaseSlice(offset, length));
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.That(pool.ReturnCount, Is.Zero);
    }

    [TestCase(-1, 1)]
    [TestCase(4, 1)]
    [TestCase(0, -1)]
    [TestCase(1, int.MaxValue)]
    public void NestedSliceIsBoundedByItsLease(int offset, int length)
    {
        TrackingPool pool = new();
        using PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        using PooledBuffer.Slice slice = buffer[2..6];
        Assert.Throws<ArgumentOutOfRangeException>(() => slice.LeaseSlice(offset, length));
        Assert.That(buffer.RefCount, Is.EqualTo(2));
        Assert.That(pool.ReturnCount, Is.Zero);
    }

    [Test]
    public void NegativeRentLengthIsRejectedBeforeCallingThePool()
    {
        TrackingPool pool = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => PooledBuffer.Rent(-1, pool));
        Assert.That(pool.RentCount, Is.Zero);
    }

    [Test]
    public void AnExistingInternalReferenceCanRetainAfterRootDisposal()
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        buffer.Span[0] = 42;
        buffer.Retain();
        buffer.Dispose();
        buffer.Retain();
        buffer.Release();
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.That(buffer.GetMemory(0, 1).Span[0], Is.EqualTo(42));
        Assert.That(pool.ReturnCount, Is.Zero);
        buffer.Release();
        Assert.That(buffer.RefCount, Is.Zero);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FinalReleaseCannotBeResurrectedWhileReturningToThePool(bool releaseSlice)
    {
        TaskCompletionSource returning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim finishReturn = new();
        TrackingPool pool = new()
        {
            BeforeReturn = () =>
            {
                returning.TrySetResult();
                if (!finishReturn.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Pool return was not released.");
            },
        };
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        PooledBuffer.Slice? slice = releaseSlice ? buffer[1..7] : null;
        if (slice is not null) buffer.Dispose();
        Task disposing = Task.Run(() =>
        {
            if (slice is not null) slice.Dispose();
            else buffer.Dispose();
        });
        try
        {
            await returning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(buffer.RefCount, Is.Zero);
            Assert.Throws<ObjectDisposedException>(buffer.Retain);
            Assert.Throws<ObjectDisposedException>(buffer.Retain);
            Assert.That(buffer.RefCount, Is.Zero, "Failed retention must not increment the reference count.");
            if (slice is not null)
            {
                Assert.Throws<ObjectDisposedException>(() => { _ = slice[0..1]; });
                Assert.Throws<ObjectDisposedException>(() => { _ = slice.Memory; });
                slice.Dispose();
            }
            Assert.Throws<ObjectDisposedException>(() => { _ = buffer[0..1]; });
            Assert.Throws<ObjectDisposedException>(() => { _ = buffer.Memory; });
            buffer.Dispose();
            Assert.That(buffer.RefCount, Is.Zero);
        }
        finally
        {
            finishReturn.Set();
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentAliasDisposalReleasesEachLeaseOnlyOnce()
    {
        TrackingPool pool = new();
        PooledBuffer buffer = PooledBuffer.Rent(8, pool);
        PooledBuffer.Slice slice = buffer[1..7];
        using ManualResetEventSlim dispose = new();
        Task first = Task.Run(() => { dispose.Wait(); buffer.Dispose(); slice.Dispose(); });
        Task second = Task.Run(() => { dispose.Wait(); slice.Dispose(); buffer.Dispose(); });
        dispose.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(buffer.RefCount, Is.Zero);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public const int ExtraCapacity = 13;
        private int _returnCount;
        public byte[]? Rented { get; private set; }
        public int RentCount { get; private set; }
        public int ReturnCount => Volatile.Read(ref _returnCount);
        public int ReturnedLength { get; private set; }
        public Action? BeforeReturn { get; init; }

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            Rented = new byte[minimumLength + ExtraCapacity];
            Array.Fill(Rented, (byte)0xcc);
            return Rented;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (!ReferenceEquals(array, Rented)) throw new InvalidOperationException("Returned a different array.");
            Interlocked.Increment(ref _returnCount);
            ReturnedLength = array.Length;
            BeforeReturn?.Invoke();
            Array.Fill(array, (byte)0xdd);
        }
    }
}
