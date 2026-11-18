// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Context;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

internal class SessionContextCancellationTests
{
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public async Task NativeContextForwardsCancellationForEveryDialOverload(int overload, bool preCanceled)
    {
        NestedProtocol protocol = new();
        ProtocolRef protocolRef = new(protocol);
        await using LocalPeer peer = new(TestPeers.Identity(1), null,
            new ProtocolStackSettings { Protocols = new() { [protocolRef] = [] } });
        LocalPeer.Session session = new(peer);
        ISessionContext context = new SessionContext(peer, session, protocolRef, false, null, null, null);
        using CancellationTokenSource cancellation = new();
        if (preCanceled) cancellation.Cancel();
        try
        {
            Task dial = overload switch
            {
                0 => context.DialAsync<NestedProtocol>(cancellation.Token),
                1 => context.DialAsync(protocol, cancellation.Token),
                2 => context.DialAsync<NestedProtocol, int>(cancellation.Token),
                _ => context.DialAsync<NestedProtocol, string, int>("request", cancellation.Token)
            };
            if (!preCanceled)
            {
                UpgradeOptions request = session.GetRequestQueue().First();
                Assert.That(request.SelectedProtocol, Is.SameAs(protocol));
                Assert.That(request.CancellationToken, Is.EqualTo(cancellation.Token));
                Assert.That(request.Argument, Is.EqualTo(overload == 3 ? "request" : null));
                cancellation.Cancel();
                Assert.That(request.CompletionSource!.Task.IsCanceled, Is.True);
            }
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(session.QueuedRequestCount, Is.Zero);
        }
        finally { await session.DisconnectAsync(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NestedDialsDoNotImplicitlyInheritTheParentToken(bool instance)
    {
        NestedProtocol protocol = new();
        ProtocolRef protocolRef = new(protocol);
        await using LocalPeer peer = new(TestPeers.Identity(1), null,
            new ProtocolStackSettings { Protocols = new() { [protocolRef] = [] } });
        LocalPeer.Session session = new(peer);
        ISessionContext context = new SessionContext(peer, session, protocolRef, false,
            new UpgradeOptions { CancellationToken = new(true) }, null, null);
        try
        {
            Task dial = instance ? context.DialAsync(protocol) : context.DialAsync<NestedProtocol>();
            UpgradeOptions request = session.GetRequestQueue().First();
            Assert.That(request.CancellationToken, Is.EqualTo(CancellationToken.None));
            request.CompletionSource!.SetResult(null);
            await dial;
        }
        finally { await session.DisconnectAsync(); }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task LegacyContextCanCancelNewOverloadsWithoutImplementingThem(bool instance, bool preCanceled)
    {
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings());
        LocalPeer.Session session = new(peer);
        LegacyContext legacy = new(peer, session);
        ISessionContext context = legacy;
        using CancellationTokenSource cancellation = new();
        if (preCanceled) cancellation.Cancel();
        try
        {
            Task dial = instance ? context.DialAsync(new NestedProtocol(), cancellation.Token)
                : context.DialAsync<NestedProtocol>(cancellation.Token);
            Assert.That(legacy.Calls, Is.EqualTo(preCanceled ? 0 : 1));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(legacy.Completion.Task.IsCompleted, Is.False, "The legacy fallback can only cancel the wait.");
            legacy.Completion.SetResult();
        }
        finally
        {
            legacy.Completion.TrySetResult();
            await session.DisconnectAsync();
        }
    }

    private sealed class LegacyContext(LocalPeer peer, LocalPeer.Session session)
        : ConnectionContext(peer, session, new(new NestedProtocol()), false, null, null, null), ISessionContext
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol => DialAsync(new NestedProtocol());
        public Task DialAsync(ISessionProtocol protocol)
        {
            Calls++;
            return Completion.Task;
        }
        public Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default)
            where TProtocol : ISessionProtocol<TRequest, TResponse> => throw new NotSupportedException();
    }

    private sealed class NestedProtocol : ISessionProtocol<int>, ISessionProtocol<string, int>
    {
        public string Id => "/test/nested-cancellation/1.0.0";
        public Task<int> DialAsync(IChannel channel, ISessionContext context) => Task.FromResult(42);
        public Task<int> DialAsync(IChannel channel, ISessionContext context, string request) => Task.FromResult(42);
        public Task ListenAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
    }
}
