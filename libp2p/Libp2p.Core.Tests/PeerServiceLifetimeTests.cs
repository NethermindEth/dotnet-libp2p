// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class PeerServiceLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [TestCase(false)]
    [TestCase(true)]
    public async Task PeerServiceResolutionDoesNotWaitForSessions(bool cached)
    {
        var settings = new ProtocolStackSettings();
        var resource = new Resource();
        int creations = 0;
        PeerServiceRegistry.Configure(settings, new()
        {
            [typeof(Resource)] = new(_ => { Interlocked.Increment(ref creations); return resource; }, true)
        });
        await using var peer = new LocalPeer(TestPeers.Identity(1), null, settings);
        if (cached) Assert.That(peer.GetPeerService<Resource>(), Is.SameAs(resource));
        using var entered = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var result = new TaskCompletionSource<Resource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            entered.Set();
            try { result.TrySetResult(peer.GetPeerService<Resource>()); }
            catch (Exception failure) { result.TrySetException(failure); }
            finally { completed.Set(); }
        })
        { IsBackground = true };
        bool progressed = false;
        try
        {
            lock (peer.Sessions)
            {
                worker.Start();
                Assert.That(entered.Wait(Deadline), Is.True);
                Assert.That(SpinWait.SpinUntil(() => completed.IsSet ||
                    (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, Deadline), Is.True);
                progressed = completed.IsSet;
            }
        }
        finally { Assert.That(worker.Join(Deadline), Is.True); }
        Assert.That(await result.Task, Is.SameAs(resource));
        Assert.That(creations, Is.EqualTo(1));
        Assert.That(progressed, Is.True, "Cold and cached peer services must resolve while Sessions is locked by another thread.");
    }

    [Test]
    public async Task DisposalIncludesServiceCreatedByAnInflightFactory()
    {
        var settings = new ProtocolStackSettings();
        var resource = new Resource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        PeerServiceRegistry.Configure(settings, new()
        {
            [typeof(Resource)] = new(_ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Factory was not released.");
                return resource;
            }, true)
        });
        await using var peer = new LocalPeer(TestPeers.Identity(1), null, settings);
        var resolution = new TaskCompletionSource<Resource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Thread(() =>
        {
            try { resolution.TrySetResult(peer.GetPeerService<Resource>()); }
            catch (Exception failure) { resolution.TrySetException(failure); }
        })
        { IsBackground = true };
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposer = new Thread(() =>
        {
            try { peer.DisposeAsync().AsTask().GetAwaiter().GetResult(); disposal.TrySetResult(); }
            catch (Exception failure) { disposal.TrySetException(failure); }
        })
        { IsBackground = true };
        resolver.Start();
        bool disposalStarted = false;
        try
        {
            Assert.That(entered.Wait(Deadline), Is.True);
            disposer.Start();
            disposalStarted = true;
            // The public admission guard proves shutdown started before the factory returns.
            Assert.That(SpinWait.SpinUntil(Disposing, Deadline), Is.True);
            Assert.That(disposal.Task.IsCompleted, Is.False);
            Assert.That(resource.Disposals, Is.Zero);
        }
        finally
        {
            release.Set();
            Assert.That(resolver.Join(Deadline), Is.True);
            if (disposalStarted) Assert.That(disposer.Join(Deadline), Is.True);
        }
        Assert.That(await resolution.Task, Is.SameAs(resource));
        await disposal.Task.WaitAsync(Deadline);
        Assert.That(resource.Disposals, Is.EqualTo(1));
        await peer.DisposeAsync();
        Assert.That(resource.Disposals, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => peer.GetPeerService<Resource>());

        bool Disposing()
        {
            try { _ = peer.DialAsync(addrs: [], CancellationToken.None); return false; }
            catch (ObjectDisposedException) { return true; }
            catch (Libp2pException) { return false; }
        }
    }

    [Test]
    public async Task DisposalStartedInsideFactoryIncludesItsResult()
    {
        var settings = new ProtocolStackSettings();
        var resource = new Resource();
        Task? disposal = null;
        PeerServiceRegistry.Configure(settings, new()
        {
            [typeof(Resource)] = new(owner =>
            {
                disposal = owner.DisposeAsync().AsTask();
                return resource;
            }, true)
        });
        await using var peer = new LocalPeer(TestPeers.Identity(1), null, settings);

        Assert.That(peer.GetPeerService<Resource>(), Is.SameAs(resource));
        Assert.That(disposal, Is.Not.Null);
        await disposal!.WaitAsync(Deadline);

        Assert.That(resource.Disposals, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => peer.GetPeerService<Resource>());
    }

    private sealed class Resource : IDisposable
    {
        public int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
