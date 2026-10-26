// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Context;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

internal class SessionResponseCompatibilityTests
{
    [TestCase(typeof(ISession))]
    [TestCase(typeof(ISessionContext))]
    public void TypedDialHasADefaultImplementation(Type sessionType)
    {
        var typedDial = sessionType.GetMethods().Single(method =>
            method.Name == nameof(ISession.DialAsync) && method.GetGenericArguments().Length == 2);

        Assert.That(typedDial.IsAbstract, Is.False, "Existing session implementations must not require a new member.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacySessionReportsUnsupportedTypedResponsesWithoutDialing(bool canceled)
    {
        LegacySession legacy = new();
        ISession session = legacy;

        Task<string> result = session.DialAsync<ResponseProtocol, string>(new CancellationToken(canceled));

        Assert.That(result.IsFaulted, Is.True);
        Assert.ThrowsAsync<NotSupportedException>(async () => await result);
        Assert.That(legacy.DialCount, Is.Zero);
        await session.DialAsync<ResponseProtocol>();
        Assert.That(legacy.DialCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacySessionContextReportsUnsupportedTypedResponsesWithoutDialing(bool canceled)
    {
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings());
        LocalPeer.Session native = new(peer);
        LegacySessionContext legacy = new(peer, native);
        ISessionContext context = legacy;

        Task<string> result = context.DialAsync<ResponseProtocol, string>(new CancellationToken(canceled));

        Assert.That(result.IsFaulted, Is.True);
        Assert.ThrowsAsync<NotSupportedException>(async () => await result);
        Assert.That(legacy.DialCount, Is.Zero);
        await context.DialAsync<ResponseProtocol>();
        Assert.That(legacy.DialCount, Is.EqualTo(1));
        await native.DisconnectAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeTypedDialReturnsTheProtocolResponseAndObservesCancellation(bool useContext)
    {
        ResponseProtocol protocol = new();
        ProtocolRef protocolRef = new(protocol);
        await using LocalPeer peer = new(TestPeers.Identity(1), null, new ProtocolStackSettings
        {
            Protocols = new() { [protocolRef] = [] }
        });
        LocalPeer.Session session = new(peer);
        ISessionContext context = new SessionContext(peer, session, protocolRef, false, null, null, null);
        try
        {
            Task<string> response = Dial();
            Assert.That(response.IsCompleted, Is.False);
            UpgradeOptions request = session.GetRequestQueue().First();
            Assert.That(request.SelectedProtocol, Is.SameAs(protocol));
            request.CompletionSource!.SetResult("protocol response");
            Assert.That(await response, Is.EqualTo("protocol response"));

            using CancellationTokenSource cancellation = new();
            Task<string> pending = Dial(cancellation.Token);
            Assert.That(pending.IsCompleted, Is.False);
            request = session.GetRequestQueue().First();
            Assert.That(request.CancellationToken, Is.EqualTo(cancellation.Token));
            cancellation.Cancel();
            Assert.That(request.CompletionSource!.Task.IsCanceled, Is.True);
            Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        }
        finally
        {
            await session.DisconnectAsync();
        }

        Task<string> Dial(CancellationToken token = default) => useContext
            ? context.DialAsync<ResponseProtocol, string>(token)
            : ((ISession)session).DialAsync<ResponseProtocol, string>(token);
    }

    private sealed class LegacySession : ISession
    {
        public Multiaddress RemoteAddress => TestPeers.Multiaddr(2);
        public Activity? Activity => null;
        public int DialCount { get; private set; }
        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
        {
            DialCount++;
            return Task.CompletedTask;
        }
        public Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default)
            where TProtocol : ISessionProtocol<TRequest, TResponse> => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
    }

    private sealed class LegacySessionContext(LocalPeer peer, LocalPeer.Session session)
        : ConnectionContext(peer, session, new ProtocolRef(new ResponseProtocol()), false, null, null, null), ISessionContext
    {
        public int DialCount { get; private set; }
        public Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol => DialAsync(new ResponseProtocol());
        public Task DialAsync(ISessionProtocol protocol)
        {
            DialCount++;
            return Task.CompletedTask;
        }
        public Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default)
            where TProtocol : ISessionProtocol<TRequest, TResponse> => throw new NotSupportedException();
    }

    private sealed class ResponseProtocol : ISessionProtocol<string>
    {
        public string Id => "/test/response/1.0.0";
        public Task<string> DialAsync(IChannel downChannel, ISessionContext context) => Task.FromResult("protocol response");
        public Task ListenAsync(IChannel downChannel, ISessionContext context) => Task.CompletedTask;
    }
}
