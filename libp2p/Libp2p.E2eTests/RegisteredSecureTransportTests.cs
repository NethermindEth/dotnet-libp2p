// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net.Quic;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class RegisteredSecureTransportTests
{
    [TestCase("noise")]
    [TestCase("tls")]
    [TestCase("quic")]
    public async Task RepeatedMutualAuthenticationSurvivesFailedDialAndKeyCollection(string transport)
    {
        if (transport == "quic" && !QuicListener.IsSupported)
        {
            if (OperatingSystem.IsWindows()) Assert.Fail("The Windows transport runner must support QUIC.");
            Assert.Inconclusive("QUIC is not supported in this environment.");
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await using ServiceProvider services = Services(transport);
        await using ILocalPeer listener = services.GetRequiredService<IPeerFactory>().Create();
        string listenAddress = transport == "quic" ? "/ip4/127.0.0.1/udp/0/quic-v1" : "/ip4/127.0.0.1/tcp/0";
        await listener.StartListenAsync([listenAddress], deadline.Token);
        System.Threading.Channels.Channel<ISession> accepted = System.Threading.Channels.Channel.CreateUnbounded<ISession>();
        listener.OnConnected += session => accepted.Writer.TryWrite(session);
        Multiaddress destination = listener.ListenAddresses.Single();

        // Reuse the registered protocol instance and its session key across attempts.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using ILocalPeer dialer = services.GetRequiredService<IPeerFactory>().Create();
            Multiaddress wrongPeer = destination.ToString().Replace($"/p2p/{listener.Identity.PeerId}", $"/p2p/{TestPeers.PeerId(99)}");
            Exception? failure = Assert.CatchAsync(async () => await dialer.DialAsync(wrongPeer, deadline.Token).WaitAsync(deadline.Token));
            Assert.That(failure, Is.TypeOf<TaskCanceledException>().Or.TypeOf<PeerConnectionException>());
            Assert.That(deadline.IsCancellationRequested, Is.False);

            ISession outbound = await dialer.DialAsync(destination, deadline.Token).WaitAsync(deadline.Token);
            ISession inbound = await accepted.Reader.ReadAsync(deadline.Token);
            Assert.That(outbound.RemotePeerId, Is.EqualTo(listener.Identity.PeerId));
            Assert.That(inbound.RemotePeerId, Is.EqualTo(dialer.Identity.PeerId));
            // Collect temporary certificate/key wrappers after authentication. Native TLS
            // and QUIC owners must still be able to exchange application data both ways.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.That(await outbound.DialAsync<IncrementNumberTestProtocol, int, int>(41, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(42));
            Assert.That(await inbound.DialAsync<IncrementNumberTestProtocol, int, int>(51, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(52));
        }
    }

    internal static ServiceProvider Services(string transport)
    {
        IServiceCollection services = new ServiceCollection();
        if (transport == "tls")
            services.AddLibp2p<RegisteredIdentityTests.TlsBuilder>(builder => builder.AddProtocol<IncrementNumberTestProtocol>());
        else
            services.AddLibp2p(builder =>
            {
                if (transport == "quic") builder.WithQuic();
                return builder.AddProtocol<IncrementNumberTestProtocol>();
            });
        return services.BuildServiceProvider();
    }
}
