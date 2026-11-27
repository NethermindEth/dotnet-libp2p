// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Nethermind.Libp2p.E2eTests;

public class SessionRecoveryTests
{
    [Test]
    public async Task RecoverAfterRemoteTcpDisconnectAndStopAfterLocalDisconnect()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder.WithSessionRecovery(new SessionRecoveryOptions(retryDelay: _ => TimeSpan.Zero)))
            .BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer client = factory.Create();
        await using ILocalPeer server = factory.Create();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        await server.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
        services.GetRequiredService<IPeerStore>().Discover([.. server.ListenAddresses]);
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = client.GetSessionRecovery()
            .WatchAsync(server.Identity.PeerId, deadline.Token).GetAsyncEnumerator();

        ISession original = await NextReadyAsync();
        await original.DialAsync<PingProtocol>(deadline.Token);
        ISession remote = await server.DialAsync(client.Identity.PeerId, deadline.Token);
        await remote.DisconnectAsync().WaitAsync(deadline.Token);

        ISession replacement = await NextReadyAsync();
        Assert.That(original.Closed.IsCancellationRequested, Is.True);
        Assert.That(original.CloseReason, Is.EqualTo(SessionCloseReason.ConnectionLost));
        Assert.That(replacement, Is.Not.SameAs(original));
        await replacement.DialAsync<PingProtocol>(deadline.Token);
        await replacement.DisconnectAsync().WaitAsync(deadline.Token);
        Assert.That(await watch.MoveNextAsync(), Is.True);
        Assert.That(watch.Current.State, Is.EqualTo(SessionRecoveryState.Stopped));
        Assert.That(watch.Current.CloseReason, Is.EqualTo(SessionCloseReason.LocalDisconnect));
        Assert.That(await watch.MoveNextAsync(), Is.False);

        async Task<ISession> NextReadyAsync()
        {
            while (await watch.MoveNextAsync())
            {
                if (watch.Current.Session is { } session) return session;
                Assert.That(watch.Current.IsTerminal, Is.False, watch.Current.ToString());
            }
            throw new AssertionException("Recovery ended before a session became ready.");
        }
    }
}
