// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
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
        IConnectionContext dialerContext = Context(1, 2, dialerUp, lifetime.Token);
        IConnectionContext listenerContext = Context(2, 1, listenerUp, lifetime.Token);
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

    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    public async Task NoiseEofIsOrderlyOnlyBetweenCompleteRecords(bool listenerReceivesEof, int prefixBytes)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        Channel transport = new();
        Channel dialerUp = new();
        Channel listenerUp = new();
        Task listen = new NoiseProtocol().ListenAsync(transport, Context(2, 1, listenerUp, lifetime.Token));
        Task dial = new NoiseProtocol().DialAsync(transport.Reverse, Context(1, 2, dialerUp, lifetime.Token));
        try
        {
            Task send = dialerUp.Reverse.WriteVarintAsync(42).AsTask();
            Assert.That(await listenerUp.Reverse.ReadVarintAsync(lifetime.Token), Is.EqualTo(42));
            await send;
            IChannel remote = listenerReceivesEof ? transport.Reverse : transport;
            if (prefixBytes != 0)
                await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0, 32 }.AsMemory(0, prefixBytes)), lifetime.Token);
            await remote.WriteEofAsync(lifetime.Token);
            await Task.WhenAll(listen, dial).WaitAsync(lifetime.Token);
            IChannel receiver = listenerReceivesEof ? listenerUp.Reverse : dialerUp.Reverse;
            Assert.That((await receiver.ReadAsync(1)).Result, Is.EqualTo(prefixBytes == 0 ? IOResult.Ended : IOResult.Aborted));
        }
        finally
        {
            lifetime.Cancel();
            await transport.AbortAsync();
            await dialerUp.AbortAsync();
            await listenerUp.AbortAsync();
            await Task.WhenAll(listen, dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NoiseAcceptsFragmentedPrefixesAndPayloads(bool listenerSends)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        Channel transport = new();
        Channel dialerUp = new();
        Channel listenerUp = new();
        Task listen = new NoiseProtocol().ListenAsync(new FragmentedWrites(transport), Context(2, 1, listenerUp, lifetime.Token));
        Task dial = new NoiseProtocol().DialAsync(new FragmentedWrites(transport.Reverse), Context(1, 2, dialerUp, lifetime.Token));
        try
        {
            IChannel sender = listenerSends ? listenerUp.Reverse : dialerUp.Reverse;
            IChannel receiver = listenerSends ? dialerUp.Reverse : listenerUp.Reverse;
            Task send = sender.WriteVarintAsync(42).AsTask();
            Assert.That(await receiver.ReadVarintAsync(lifetime.Token), Is.EqualTo(42));
            await send;
            await sender.WriteEofAsync(lifetime.Token);
            await Task.WhenAll(listen, dial).WaitAsync(lifetime.Token);
            Assert.That((await receiver.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
        }
        finally
        {
            lifetime.Cancel();
            await transport.AbortAsync();
            await dialerUp.AbortAsync();
            await listenerUp.AbortAsync();
            await Task.WhenAll(listen, dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class FragmentedWrites(IChannel inner) : IChannel
    {
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
            => inner.ReadAsync(length, blockingMode, token);

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            if (bytes.Length <= 1) return await inner.WriteAsync(bytes, token);
            IOResult first = await inner.WriteAsync(bytes.Slice(0, 1), token);
            return first == IOResult.Ok ? await inner.WriteAsync(bytes.Slice(1), token) : first;
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }

    private static IConnectionContext Context(int local, int remote, Channel up, CancellationToken token)
    {
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(TestPeers.Identity(local));
        context.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(local)]);
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(remote) });
        context.Token.Returns(token);
        context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(up);
        return context;
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
