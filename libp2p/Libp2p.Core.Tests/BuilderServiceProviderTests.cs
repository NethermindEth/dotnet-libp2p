// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class BuilderServiceProviderTests
{
    [Test]
    public async Task ConcurrentSingletonResolutionRunsOneFactory()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var services = new ServiceCollection().AddSingleton(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
            return new Resource();
        });
        await using var provider = new BuilderServiceProvider(services, null);
        var first = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread firstThread = Worker(first);
        Thread secondThread = Worker(second);
        bool firstStarted = false;
        bool secondStarted = false;
        try
        {
            firstThread.Start();
            firstStarted = true;
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            secondThread.Start();
            secondStarted = true;
            Assert.That(SpinWait.SpinUntil(() => (secondThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)), Is.True);
        }
        finally
        {
            release.Set();
            if (firstStarted) Assert.That(firstThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            if (secondStarted) Assert.That(secondThread.Join(TimeSpan.FromSeconds(5)), Is.True);
        }
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(await first.Task, Is.SameAs(await second.Task));
        await provider.DisposeAsync();
        Assert.That((await first.Task).Disposals, Is.EqualTo(1));

        Thread Worker(TaskCompletionSource<Resource> result) => new(() =>
        {
            try { result.TrySetResult(provider.GetRequiredService<Resource>()); }
            catch (Exception ex) { result.TrySetException(ex); }
        })
        { IsBackground = true };
    }

    [Test]
    public async Task FailedSingletonFactoryCanBeRetried()
    {
        int calls = 0;
        var failure = new InvalidOperationException("First construction failed.");
        var services = new ServiceCollection().AddSingleton(_ =>
        {
            if (++calls == 1) throw failure;
            return new Resource();
        });
        await using var provider = new BuilderServiceProvider(services, null);
        Assert.That(Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<Resource>()), Is.SameAs(failure));
        var resource = provider.GetRequiredService<Resource>();
        Assert.That(provider.GetRequiredService<Resource>(), Is.SameAs(resource));
        Assert.That(calls, Is.EqualTo(2));
        await provider.DisposeAsync();
        Assert.That(resource.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task DisposalDrainsActiveFactoryAndConcurrentDisposalHasOneOwner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var resource = new Resource();
        var services = new ServiceCollection().AddSingleton(_ =>
        {
            entered.TrySetResult();
            release.Wait();
            return resource;
        });
        await using var provider = new BuilderServiceProvider(services, null);
        Task<Resource> pending = Task.Run(provider.GetRequiredService<Resource>);
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = provider.DisposeAsync().AsTask();
            Assert.That(provider.DisposeAsync().AsTask(), Is.SameAs(disposal));
            Assert.That(disposal.IsCompleted, Is.False);
            Assert.Throws<ObjectDisposedException>(() => provider.GetRequiredService<Resource>());
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(resource.Disposals, Is.EqualTo(1));
    }

    public sealed class Resource : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
