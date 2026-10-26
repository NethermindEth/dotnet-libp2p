// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.E2eTests;

public class ProtocolLifetimeTests
{
    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    public async Task SecureForwardersPreserveEofAndJoinBothDirections(bool tls, int ending)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        Channel transport = new();
        Channel dialerUp = new();
        Channel listenerUp = new();
        IConnectionContext dialerContext = Context(1, 2, dialerUp);
        IConnectionContext listenerContext = Context(2, 1, listenerUp);
        IConnectionProtocol dialer = tls ? new TlsProtocol() : new NoiseProtocol();
        IConnectionProtocol listener = tls ? new TlsProtocol() : new NoiseProtocol();
        Task listen = listener.ListenAsync(transport, listenerContext);
        Task dial = dialer.DialAsync(transport.Reverse, dialerContext);
        try
        {
            Task send = dialerUp.Reverse.WriteVarintAsync(42).AsTask();
            Assert.That(await listenerUp.Reverse.ReadVarintAsync(lifetime.Token), Is.EqualTo(42));
            await send;
            if (ending == 0) await dialerUp.Reverse.WriteEofAsync(lifetime.Token);
            else if (ending == 1) lifetime.Cancel();
            else await transport.AbortAsync();
            await Task.WhenAll(listen, dial).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That((await listenerUp.Reverse.ReadAsync(1)).Result, Is.EqualTo(ending == 0 ? IOResult.Ended : IOResult.Aborted));
        }
        finally
        {
            lifetime.Cancel();
            await transport.AbortAsync();
            await dialerUp.AbortAsync();
            await listenerUp.AbortAsync();
            await Task.WhenAll(listen, dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        IConnectionContext Context(int local, int remote, Channel up)
        {
            IConnectionContext context = Substitute.For<IConnectionContext>();
            context.Peer.Identity.Returns(TestPeers.Identity(local));
            context.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(local)]);
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(remote) });
            context.Token.Returns(lifetime.Token);
            context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(up);
            return context;
        }
    }

    [Test]
    public async Task DefaultIdentifyDialReceivesTheSessionLifetime()
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        _ = services.GetRequiredService<IPeerFactory>();
        await using Initializer peer = new(services.GetRequiredService<IProtocolStackSettings>());
        using CancellationTokenSource closed = new();
        using CancellationTokenSource work = new();
        ISession session = Substitute.For<ISession>();
        session.Closed.Returns(closed.Token);
        CancellationToken received = default;
        CancellationTokenRegistration registration = default;
        session.DialAsync(Arg.Any<ISessionProtocol>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            received = call.ArgAt<CancellationToken>(1);
            registration = received.Register(work.Cancel);
            return Task.Delay(Timeout.InfiniteTimeSpan, work.Token);
        });
        Task initialization = peer.Initialize(session);
        try
        {
            Assert.That(received, Is.EqualTo(closed.Token));
            closed.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await initialization.WaitAsync(TimeSpan.FromSeconds(2)));
            _ = session.Received(1).DialAsync(Arg.Is<ISessionProtocol>(p => p.Id == IdentifyProtocol.ProtocolId), closed.Token);
        }
        finally { registration.Dispose(); work.Cancel(); await initialization.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    private sealed class Initializer(IProtocolStackSettings stack)
        : Libp2pPeer(stack, new PeerStore(), new Identity(), new IdentifyNotifier())
    {
        public Task Initialize(ISession session) => base.ConnectedTo(session, true);
    }
}
