// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Channels.Tests;

public class AsyncSemaphoreTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CancellationDoesNotConsumeAnAvailablePermit(bool close)
    {
        AsyncSemaphore semaphore = new(true);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        ValueTask wait = close ? semaphore.WaitAsync(default, cancelled.Token) : semaphore.WaitAsync(cancelled.Token);
        OperationCanceledException? exception = Assert.Catch<OperationCanceledException>(() => wait.GetAwaiter().GetResult());
        Assert.That(exception!.CancellationToken, Is.EqualTo(cancelled.Token));
        Assert.That(semaphore.TryWait(), Is.True);
        Assert.That(semaphore.TryWait(), Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task CancellingAQueuedWaitPreservesTheRemainingOrder(int cancelledIndex)
    {
        AsyncSemaphore semaphore = new(false);
        using CancellationTokenSource cancellation = new();
        ValueTask[] waits = Enumerable.Range(0, 3)
            .Select(i => semaphore.WaitAsync(i == cancelledIndex ? cancellation.Token : default)).ToArray();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => waits[cancelledIndex].GetAwaiter().GetResult());

        for (int i = 0; i < waits.Length; i++)
        {
            if (i == cancelledIndex) continue;
            Assert.That(waits[i].IsCompleted, Is.False);
            semaphore.Release();
            Assert.That(waits[i].IsCompletedSuccessfully, Is.True);
            await waits[i];
        }
        Assert.That(semaphore.TryWait(), Is.False);
        semaphore.Release();
        Assert.That(semaphore.TryWait(), Is.True);
    }

    [Test]
    public async Task GrantedWaitWinsOverLaterCancellationAndIsNotReusedBeforeConsumption()
    {
        AsyncSemaphore semaphore = new(false);
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource closed = new();
        ValueTask first = semaphore.WaitAsync(cancellation.Token, closed.Token);
        semaphore.Release();
        cancellation.Cancel();
        closed.Cancel();

        // Exercise reuse while an earlier, already granted result remains unconsumed.
        for (int i = 0; i < 10; i++)
        {
            ValueTask later = semaphore.WaitAsync(default);
            Assert.That(later.IsCompleted, Is.False);
            semaphore.Release();
            await later;
        }
        await first;
        Assert.That(semaphore.TryWait(), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletedWaitReleasesBothRegistrationsBeforeReuse(bool cancelClosed)
    {
        AsyncSemaphore semaphore = new(false);
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource closed = new();
        ValueTask first = semaphore.WaitAsync(cancellation.Token, closed.Token);
        semaphore.Release();
        await first;

        ValueTask next = semaphore.WaitAsync(default);
        (cancelClosed ? closed : cancellation).Cancel();
        Assert.That(next.IsCompleted, Is.False);
        semaphore.Release();
        await next;
    }

    [Test]
    public async Task CloseCancelsEveryQueuedWaitAndLeavesNoStaleQueueEntries()
    {
        AsyncSemaphore semaphore = new(false);
        using CancellationTokenSource closed = new();
        ValueTask[] waits = Enumerable.Range(0, 8).Select(_ => semaphore.WaitAsync(default, closed.Token)).ToArray();
        closed.Cancel();
        foreach (ValueTask wait in waits)
            Assert.Throws<OperationCanceledException>(() => wait.GetAwaiter().GetResult());

        ValueTask next = semaphore.WaitAsync(default);
        Assert.That(next.IsCompleted, Is.False);
        semaphore.Release();
        await next;
        Assert.That(semaphore.TryWait(), Is.False);
    }

    [Test]
    public async Task ConcurrentCancellationAndReleaseNeverLoseOrDuplicateAPermit()
    {
        AsyncSemaphore semaphore = new(false);
        for (int i = 0; i < 200; i++)
        {
            using CancellationTokenSource cancellation = new();
            using CancellationTokenSource closed = new();
            ValueTask wait = semaphore.WaitAsync(cancellation.Token, closed.Token);
            Task cancel = Task.Run(cancellation.Cancel);
            Task close = Task.Run(closed.Cancel);
            Task release = Task.Run(semaphore.Release);
            bool cancelled = false;
            try { await wait; }
            catch (OperationCanceledException) { cancelled = true; }
            await Task.WhenAll(cancel, close, release);
            Assert.That(semaphore.TryWait(), Is.EqualTo(cancelled));
            Assert.That(semaphore.TryWait(), Is.False);
        }
    }

    [Test]
    public async Task ReleaseDoesNotInvokeTheConsumerInline()
    {
        AsyncSemaphore semaphore = new(false);
        TaskCompletionSource<bool> resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int releasingThread = Environment.CurrentManagedThreadId;
        bool releasing = false;
        var awaiter = semaphore.WaitAsync(default).ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            bool inline = Environment.CurrentManagedThreadId == releasingThread && releasing;
            awaiter.GetResult();
            resumed.SetResult(inline);
        });
        releasing = true;
        semaphore.Release();
        releasing = false;
        Assert.That(await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
    }

    [Test]
    public void WarmCancellableHandoffsDoNotAllocatePerWait()
    {
        AsyncSemaphore semaphore = new(false);
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource closed = new();
        Handoff(10);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Handoff(1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero);

        void Handoff(int count)
        {
            for (int i = 0; i < count; i++)
            {
                ValueTask wait = semaphore.WaitAsync(cancellation.Token, closed.Token);
                semaphore.Release();
                wait.GetAwaiter().GetResult();
            }
        }
    }
}
