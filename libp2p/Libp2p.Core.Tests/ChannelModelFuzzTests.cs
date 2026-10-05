// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Reflection;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
/// Deterministic model fuzzer for <see cref="Channel"/> and its nested
/// <c>ReaderWriter</c>: seeded transfer scripts with exact byte/result oracles
/// plus a systematic race matrix (parked op × close/abort/cancel/EOF/data).
/// Every await is time-bounded so a hang fails loudly instead of hanging CI.
/// No new dependencies.
/// </summary>
[TestFixture]
public class ChannelModelFuzzTests
{
    /// <summary>Committed corpus: seeds for the transfer scripts.</summary>
    public static IEnumerable<int> ScriptSeeds()
    {
        for (int seed = 5001; seed <= 5020; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(ScriptSeeds))]
    public async Task Script_TransferThenLifecycle(int seed) =>
        await RunTransferScript(seed);

    [TestCaseSource(nameof(ScriptSeeds))]
    public async Task Script_AbortMidTransfer(int seed) =>
        await RunAbortMidTransferScript(seed);

    [Test]
    public async Task Race_ParkedRead_CloseWithoutPendingIo_EndsGracefully()
    {
        Channel channel = new();
        Task<ReadResult> read = channel.ReadAsync(1).AsTask();
        await Task.Delay(50);
        await channel.CloseAsync();

        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Race_ParkedRead_CloseWithPendingWrite_Aborts()
    {
        Channel channel = new();
        // Parked operations must sit on OPPOSITE directions: a write and a read
        // on the same rendezvous would pair up instead of both parking.
        // The parked write counts as pending outbound work, so closing must
        // abort the parked reader (not end it).
        Task<IOResult> parkedWrite = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 9 })).AsTask();
        Task<ReadResult> parkedRead = channel.ReadAsync(1).AsTask();
        await Task.Delay(50);
        await channel.CloseAsync();

        Assert.That((await parkedRead.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await parkedWrite.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Race_ParkedRead_Abort_Aborts()
    {
        Channel channel = new();
        Task<ReadResult> read = channel.ReadAsync(1).AsTask();
        await Task.Delay(50);
        await channel.AbortAsync();

        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task Race_ParkedRead_WriteEof_EndsGracefully()
    {
        Channel channel = new();
        Task<ReadResult> read = channel.ReadAsync(1).AsTask();
        await Task.Delay(50);
        Assert.That(await channel.Reverse.WriteEofAsync(), Is.EqualTo(IOResult.Ok));

        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Race_ParkedRead_MatchingWrite_Delivers()
    {
        Channel channel = new();
        Task<ReadResult> read = channel.ReadAsync(2).AsTask();
        await Task.Delay(50);
        Task<IOResult> write = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 7, 8 })).AsTask();

        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(), Is.EqualTo(new byte[] { 7, 8 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task Race_ParkedRead_Cancel_KeepsChannelUsable()
    {
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Task<ReadResult> read = channel.ReadAsync(1, token: cancel.Token).AsTask();
        await Task.Delay(50);
        cancel.Cancel();

        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Cancelled));

        Task<IOResult> write = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 3 })).AsTask();
        Assert.That((await channel.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 3 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task Race_ParkedWrite_Close_EndsWrite()
    {
        Channel channel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await Task.Delay(50);
        await channel.CloseAsync();

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ended));
        Assert.That((await channel.Reverse.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result,
            Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task Race_ParkedWrite_Abort_EndsWrite()
    {
        // Pinned convention: plain writes report Ended (not Aborted) after abort;
        // only reads and EOF preserve the abort cause.
        Channel channel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await Task.Delay(50);
        await channel.AbortAsync();

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Race_ParkedWrite_MatchingRead_CompletesRendezvous()
    {
        Channel channel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 4, 5 })).AsTask();
        await Task.Delay(50);
        Task<ReadResult> read = channel.Reverse.ReadAsync(2).AsTask();

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 4, 5 }));
    }

    [Test]
    public async Task Race_ParkedWrite_Cancel_KeepsChannelUsable()
    {
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token: cancel.Token).AsTask();
        await Task.Delay(50);
        cancel.Cancel();

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Cancelled));

        Task<IOResult> write2 = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 })).AsTask();
        Assert.That((await channel.Reverse.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 2 }));
        Assert.That(await write2.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task Race_ParkedEofBehindParkedWrite_AbortsOnClose()
    {
        Channel channel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await Task.Delay(50);
        Task<IOResult> eof = channel.WriteEofAsync().AsTask();
        await Task.Delay(50);
        await channel.CloseAsync();

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ended));
        Assert.That(await eof.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task WriteAfterEof_AlwaysEndsGracefully()
    {
        Channel channel = new();
        Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        // Even with (undeliverable) payload: the EOF check precedes the buffer check.
        Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 })),
            Is.EqualTo(IOResult.Ended));
        Assert.That(await channel.WriteAsync(default), Is.EqualTo(IOResult.Ended));
        Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task DoNotWait_ReturnsAvailableImmediately()
    {
        // DoNotWait sizes like WaitAny (min of request and buffer) and never
        // parks for the remainder: with partial data it returns what there is.
        Channel channel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        Assert.That((await channel.Reverse.ReadAsync(5, ReadBlockingMode.DoNotWait)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 1 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));

        Task<IOResult> rest = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2, 3, 4, 5, 6 })).AsTask();
        Assert.That((await channel.Reverse.ReadAsync(5).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 2, 3, 4, 5, 6 }));
        Assert.That(await rest.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));

        await channel.CloseAsync();
        Assert.That((await channel.Reverse.ReadAsync(5, ReadBlockingMode.DoNotWait)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result,
            Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task PartialWaitAll_EofDropsPartialData()
    {        // Documents a deliberate design tradeoff: a WaitAll read that already
        // took partial data reports the terminal (not partial data) when EOF
        // lands, because WaitAll callers require exact lengths and could not
        // use a short read. The taken bytes are discarded with the read.
        Channel channel = new();
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 })).AsTask();
        Task<ReadResult> partial = channel.Reverse.ReadAsync(5).AsTask();
        await Task.Delay(100);
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
        await channel.WriteEofAsync();

        Assert.That((await partial.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
        Assert.That((await channel.Reverse.ReadAsync(5).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result,
            Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task CancelledWrite_RollsBackPublish()
    {
        // Cancelling a parked write whose signal nobody took retracts the
        // publish: the bytes are never delivered, the next write succeeds,
        // and the channel stays usable.
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Task<IOResult> parked = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 }), cancel.Token).AsTask();
        await Task.Delay(50);
        cancel.Cancel();
        Assert.That(await parked.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Cancelled));

        // Nothing was left behind: a read still parks until fresh data arrives.
        Task<ReadResult> read = channel.Reverse.ReadAsync(2).AsTask();
        await Task.Delay(100);
        Assert.That(read.IsCompleted, Is.False, "Rolled-back bytes must not be deliverable.");
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 3, 4 })).AsTask();
        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 3, 4 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task CancelledWrite_ThenEof_ReadEndsEmpty()
    {
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Task<IOResult> parked = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 }), cancel.Token).AsTask();
        await Task.Delay(50);
        cancel.Cancel();
        Assert.That(await parked.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Cancelled));

        Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        Assert.That((await channel.Reverse.ReadAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result,
            Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task CancelRacesTake_AtLeastOnceAmbiguity()
    {
        // If a reader consumes the signal concurrently with the writer's
        // cancel, the data is delivered but the writer still reports
        // Cancelled. Either outcome is legal; nothing may hang or throw and
        // delivered bytes always come from the sent stream.
        for (int i = 0; i < 30; i++)
        {
            Channel channel = new();
            using CancellationTokenSource cancel = new();
            Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 7, 8 }), cancel.Token).AsTask();
            Task<ReadResult> read = channel.Reverse.ReadAsync(2).AsTask();
            cancel.Cancel();
            IOResult writeResult = await write.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(writeResult, Is.AnyOf(IOResult.Ok, IOResult.Cancelled), $"iter {i}");
            if (writeResult == IOResult.Ok)
            {
                Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
                    Is.EqualTo(new byte[] { 7, 8 }), $"iter {i}");
            }
            else
            {
                await channel.CloseAsync();
                ReadResult readResult = await read.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(readResult.Result, Is.AnyOf(IOResult.Ok, IOResult.Ended), $"iter {i}");
                if (readResult.Result == IOResult.Ok)
                {
                    Assert.That(readResult.Data.ToArray(), Is.EqualTo(new byte[] { 7, 8 }), $"iter {i}");
                }
            }
        }
    }

    [Test]
    public void TryWriteEof_Matrix()
    {
        Channel.ReaderWriter writer = new();
        // Free lock, no EOF: sets end-of-write and succeeds.
        Assert.That(writer.TryWriteEof(), Is.True);
        // Already at EOF: reports success without touching signals.
        Assert.That(writer.TryWriteEof(), Is.True);
    }

    [Test]
    public async Task TryWriteEof_BusyReturnsFalse()
    {
        Channel channel = new();
        Task<IOResult> parked = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await Task.Delay(50);
        // The write lock is held by the parked write: no state changes.
        Assert.That(((Channel.ReaderWriter)channel.Writer).TryWriteEof(), Is.False);
        Assert.That((await channel.Reverse.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 1 }));
        Assert.That(await parked.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task CanReadAsync_CancelledToken_ReturnsCancelled()
    {
        Channel.ReaderWriter readerWriter = new();
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Assert.That(await readerWriter.CanReadAsync(cancel.Token), Is.EqualTo(IOResult.Cancelled));
        // Channel still usable afterwards.
        Task<IOResult> write = readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        Assert.That((await readerWriter.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 1 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task CancelledWrite_AwaitingAcknowledgement_CompletesOnClose()
    {
        // Deterministic coverage for the acknowledgement wait: reflection (test
        // only, no production hook) simulates a reader that claimed the
        // data-available signal without acknowledging it. With a wait that
        // ignores cancellation the writer parks past teardown and this times
        // out; observing teardown releases it with Ended once the channel closes.
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Channel.ReaderWriter writer = (Channel.ReaderWriter)channel.Writer;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        SemaphoreSlim canRead = (SemaphoreSlim)typeof(Channel.ReaderWriter)
            .GetField("_canRead", flags)!.GetValue(writer)!;
        SemaphoreSlim acknowledged = (SemaphoreSlim)typeof(Channel.ReaderWriter)
            .GetField("_read", flags)!.GetValue(writer)!;

        Task<IOResult> write = channel.WriteAsync(
            new ReadOnlySequence<byte>(new byte[] { 1 }), cancel.Token).AsTask();
        try
        {
            // Simulate a reader that claimed the signal but has not acknowledged.
            Assert.That(canRead.Wait(0), Is.True);
            cancel.Cancel();
            await channel.CloseAsync();

            Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)),
                Is.EqualTo(IOResult.Ended));
        }
        finally
        {
            await channel.CloseAsync();
            // Also release the old implementation after a failed assertion.
            acknowledged.Release();
            await write.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task CancelledWrite_RollbackThenClose_Completes()
    {
        // Partial take, writer cancel, then close. Note what this covers: the
        // partial take re-releases the signal, so cancellation takes the
        // rollback branch (not the acknowledgement wait, which needs a
        // microsecond take/cancel interleave to engage and therefore has no
        // deterministic test). The rollback retracts the remainder, the close
        // terminates everything, and no read ever observes the retracted bytes.
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Task<IOResult> write = channel.WriteAsync(
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), cancel.Token).AsTask();
        Assert.That((await channel.Reverse.ReadAsync(3).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 1, 2, 3 }));
        cancel.Cancel();
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Cancelled));
        await channel.CloseAsync();
        Assert.That((await channel.Reverse.ReadAsync(8).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result,
            Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task CancelledWrite_RollsBackRemainder()
    {
        // Partial take, then writer cancel: the re-released signal lets the
        // rollback retract even the still-buffered remainder, so a later EOF
        // finds nothing to discard and reads end empty.
        Channel channel = new();
        using CancellationTokenSource cancel = new();
        Task<IOResult> write = channel.WriteAsync(
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), cancel.Token).AsTask();
        Assert.That((await channel.Reverse.ReadAsync(3).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 1, 2, 3 }));
        cancel.Cancel();
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Cancelled));

        Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        Assert.That((await channel.Reverse.ReadAsync(8).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result,
            Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task MultiSegmentPublish_SpanningReadPreservesBytes()
    {
        // The FIRST take itself is multi-segment, so the spanning read must
        // walk the segment copy path when a later take completes it.
        Channel.ReaderWriter readerWriter = new();
        Task<ReadResult> read = readerWriter.ReadAsync(6).AsTask();
        MemorySegment<byte> firstSegment = new(new byte[] { 2, 3 });
        MemorySegment<byte> lastSegment = firstSegment.Append(new byte[] { 4, 5 });
        Task<IOResult> firstWrite = readerWriter.WriteAsync(
            new ReadOnlySequence<byte>(firstSegment, 0, lastSegment, 2)).AsTask();
        Task<IOResult> secondWrite = readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 6, 7 })).AsTask();

        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 2, 3, 4, 5, 6, 7 }));
        Assert.That(await firstWrite.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
        Assert.That(await secondWrite.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task ZeroLengthRead_WaitAllAnswersImmediately()
    {
        Channel channel = new();
        Assert.That((await channel.ReadAsync(0, ReadBlockingMode.WaitAll)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Data.ToArray(), Is.Empty);
    }

    [Test]
    public async Task ZeroLengthRead_WaitAnyWaitsForDataThenReturnsAll()
    {
        Channel channel = new();
        Task<ReadResult> parked = channel.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask();
        await Task.Delay(100);
        Assert.That(parked.IsCompleted, Is.False, "WaitAny length-0 read on empty channel must park.");
        Task<IOResult> write = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 9, 9, 9 })).AsTask();
        Assert.That((await parked.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 9, 9, 9 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task ZeroLengthRead_DoNotWaitReportsEmpty()
    {
        Channel channel = new();
        ReadResult empty = await channel.ReadAsync(0, ReadBlockingMode.DoNotWait);
        Assert.That(empty.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(empty.Data.ToArray(), Is.Empty);
    }

    [TestCase(ReadBlockingMode.WaitAll)]
    [TestCase(ReadBlockingMode.WaitAny)]
    [TestCase(ReadBlockingMode.DoNotWait)]
    public void NegativeLength_ThrowsArgumentOutOfRange(ReadBlockingMode mode)
    {
        Channel channel = new();
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await channel.ReadAsync(-1, mode).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task ZeroLengthWrite_DoesNotDisturbFraming()
    {
        Channel channel = new();
        Assert.That(await channel.WriteAsync(default), Is.EqualTo(IOResult.Ok));
        // No signal was produced: a read still parks until real data arrives.
        Task<ReadResult> read = channel.Reverse.ReadAsync(1).AsTask();
        await Task.Delay(100);
        Assert.That(read.IsCompleted, Is.False);
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 5 })).AsTask();
        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(), Is.EqualTo(new byte[] { 5 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task ZeroLengthCycle_DoesNotCorruptSemaphores()
    {
        // Regression guard for semaphore accounting: interleaved zero-length
        // reads must never break subsequent rendezvous (no SemaphoreFull-style
        // faults, no stuck signals, exact delivery).
        Channel channel = new();
        for (int i = 0; i < 50; i++)
        {
            Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { (byte)i })).AsTask();
            Assert.That((await channel.Reverse.ReadAsync(0, ReadBlockingMode.WaitAll)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Data.ToArray(), Is.Empty);
            Assert.That((await channel.Reverse.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Data.ToArray(),
                Is.EqualTo(new byte[] { (byte)i }));
            Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(IOResult.Ok));
        }
    }

    [Test]
    public async Task HugeLengthRead_AbortsOnCloseWithoutHugeAlloc()
    {
        // WaitAll for int.MaxValue legitimately parks until close; the close
        // aborts the incomplete exact read (already-taken bytes are dropped).
        Channel channel = new();
        Task<ReadResult> read = channel.ReadAsync(int.MaxValue).AsTask();
        await Task.Delay(50);
        Task<IOResult> write = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 })).AsTask();
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
        await channel.CloseAsync();
        Assert.That((await read.WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public void Reverse_DoubleReverseIsIdentity()
    {
        Channel channel = new();
        IChannel reverse = channel.Reverse;
        Assert.That(((Channel)reverse).Reverse, Is.SameAs(channel));
        Assert.That(((Channel)((Channel)reverse).Reverse).Reverse, Is.SameAs(reverse));
    }

    [Test]
    public async Task Completion_FiresExactlyOnCloseOrDoubleEof()
    {
        Channel channel = new();
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False);
        await channel.WriteEofAsync();
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False, "Single-sided EOF must not complete.");
        await channel.Reverse.WriteEofAsync();
        Assert.That(channel.GetAwaiter().IsCompleted, Is.True, "Double EOF must complete.");
        Assert.That(channel.Reverse.GetAwaiter().IsCompleted, Is.True);

        Channel closing = new();
        Assert.That(closing.GetAwaiter().IsCompleted, Is.False);
        await closing.CloseAsync();
        Assert.That(closing.GetAwaiter().IsCompleted, Is.True);
        await closing.CloseAsync();
    }

    private static async Task RunTransferScript(int seed)
    {
        // Both directions carry pre-generated chunks; two consumers drain exact
        // totals while writes complete via rendezvous. Then one seeded lifecycle
        // event with exact terminal assertions per direction.
        Random rng = new(seed);
        Channel channel = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        int nextId = 1;

        int rounds = rng.Next(3, 8);
        List<byte[]> chunksA = [];
        List<byte[]> chunksB = [];
        for (int round = 0; round < rounds; round++)
        {
            chunksA.Add(NextChunk(rng, ref nextId));
            chunksB.Add(NextChunk(rng, ref nextId));
        }
        byte[] allA = chunksA.SelectMany(c => c).ToArray();
        byte[] allB = chunksB.SelectMany(c => c).ToArray();

        Task<byte[]> consumeA = ConsumeExactAsync(channel.Reverse, allA.Length, timeout.Token);
        Task<byte[]> consumeB = ConsumeExactAsync(channel, allB.Length, timeout.Token);
        foreach (byte[] chunk in chunksA)
        {
            Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(chunk), timeout.Token),
                Is.EqualTo(IOResult.Ok), $"seed {seed}");
        }
        foreach (byte[] chunk in chunksB)
        {
            Assert.That(await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(chunk), timeout.Token),
                Is.EqualTo(IOResult.Ok), $"seed {seed}");
        }
        Assert.That(await consumeA.WaitAsync(timeout.Token), Is.EqualTo(allA), $"seed {seed}");
        Assert.That(await consumeB.WaitAsync(timeout.Token), Is.EqualTo(allB), $"seed {seed}");

        int lifecycle = rng.Next(7);
        switch (lifecycle)
        {
            case 0:
                // Open channel stays live: prove both directions still transfer.
                byte[] ping = [(byte)(seed % 250 + 1)];
                Task<IOResult> pingWrite =
                    channel.WriteAsync(new ReadOnlySequence<byte>(ping), timeout.Token).AsTask();
                Assert.That((await channel.Reverse.ReadAsync(1, token: timeout.Token).OrThrow()).ToArray(),
                    Is.EqualTo(ping), $"seed {seed}");
                Assert.That(await pingWrite.WaitAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
                break;
            case 1:
                Assert.That(await channel.WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
                Assert.That((await channel.Reverse.ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Ended), $"seed {seed}");
                break;
            case 2:
                Assert.That(await channel.Reverse.WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
                Assert.That((await channel.ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Ended), $"seed {seed}");
                break;
            case 3:
            case 4:
                await (lifecycle == 3 ? channel : channel.Reverse).CloseAsync();
                Assert.That((await channel.ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Ended), $"seed {seed}");
                Assert.That((await channel.Reverse.ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Ended), $"seed {seed}");
                Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), timeout.Token),
                    Is.EqualTo(IOResult.Ended), $"seed {seed}");
                break;
            default:
                await (lifecycle == 5 ? channel : channel.Reverse).AbortAsync();
                Assert.That((await channel.ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Aborted), $"seed {seed}");
                Assert.That((await channel.Reverse.ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Aborted), $"seed {seed}");
                Assert.That(await channel.WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Aborted), $"seed {seed}");
                break;
        }

        await channel.CloseAsync();
        Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
    }

    private static async Task RunAbortMidTransferScript(int seed)
    {
        // Continuous writer and draining reader with unique 4-byte token ids;
        // abort lands at a seeded time. Received tokens must be an exact prefix
        // of the sent token stream with no duplicates.
        Random rng = new(seed);
        Channel channel = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        const int chunks = 30;
        List<int> sentTokens = [];
        for (int i = 0; i < chunks; i++)
        {
            int tokens = rng.Next(1, 8);
            for (int t = 0; t < tokens; t++)
            {
                sentTokens.Add(sentTokens.Count + 1);
            }
        }
        byte[] sentBytes = new byte[sentTokens.Count * 4];
        for (int i = 0; i < sentTokens.Count; i++)
        {
            BitConverter.TryWriteBytes(sentBytes.AsSpan(i * 4, 4), sentTokens[i]);
        }

        List<byte> received = [];
        object receivedLock = new();
        int abortAfterMs = rng.Next(5, 60);
        List<int> writeSizes = [];
        for (int offset = 0; offset < sentBytes.Length;)
        {
            int size = Math.Min(4 * rng.Next(1, 16), sentBytes.Length - offset);
            writeSizes.Add(size);
            offset += size;
        }
        Task writer = Task.Run(async () =>
        {
            int offset = 0;
            foreach (int size in writeSizes)
            {
                byte[] slice = new byte[size];
                sentBytes.AsSpan(offset, size).CopyTo(slice);
                IOResult result = await channel.WriteAsync(new ReadOnlySequence<byte>(slice), timeout.Token);
                if (result != IOResult.Ok)
                {
                    return;
                }
                offset += size;
            }
        });
        Task reader = Task.Run(async () =>
        {
            while (true)
            {
                ReadResult read = await channel.Reverse.ReadAsync(65536, ReadBlockingMode.WaitAny, token: timeout.Token);
                if (read.Result != IOResult.Ok)
                {
                    return;
                }
                lock (receivedLock)
                {
                    received.AddRange(read.Data.ToArray());
                }
            }
        });

        await Task.Delay(abortAfterMs);
        await channel.AbortAsync();
        await Task.WhenAll(writer, reader).WaitAsync(timeout.Token);

        List<int> receivedTokens = [];
        for (int offset = 0; offset + 4 <= received.Count; offset += 4)
        {
            receivedTokens.Add(BitConverter.ToInt32(received.ToArray(), offset));
        }
        Assert.That(received.Count % 4, Is.EqualTo(0), $"seed {seed}: torn token read");
        Assert.That(receivedTokens, Is.EqualTo(sentTokens.Take(receivedTokens.Count).ToList()),
            $"seed {seed}: received must be an exact prefix of sent");
    }

    private static byte[] NextChunk(Random rng, ref int nextId)
    {
        int size = rng.Next(1, 64);
        byte[] chunk = new byte[size];
        for (int i = 0; i < size; i++)
        {
            chunk[i] = (byte)(nextId++ % 251 + 1);
        }
        return chunk;
    }

    private static async Task<byte[]> ConsumeExactAsync(IChannel reader, int total, CancellationToken token)
    {
        byte[] collected = new byte[total];
        int offset = 0;
        while (offset < total)
        {
            ReadOnlySequence<byte> chunk = await reader.ReadAsync(total - offset, token: token).OrThrow();
            chunk.CopyTo(collected.AsSpan(offset));
            offset += (int)chunk.Length;
        }
        return collected;
    }
}
