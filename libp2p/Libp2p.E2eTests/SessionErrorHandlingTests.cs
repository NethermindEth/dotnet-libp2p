// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class SessionErrorHandlingTests
{
    [Test]
    public async Task SessionProtocolDial_ExceptionPropagatesToCaller()
    {
        await using ErrorHandlingE2eTestSetup test = new();
        await test.AddPeersAsync(2);

        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        ExpectedProtocolException? exception = Assert.ThrowsAsync<ExpectedProtocolException>(
            async () => await session.DialAsync<ThrowingSessionProtocol>());

        Assert.That(exception?.Message, Is.EqualTo(ThrowingSessionProtocol.ErrorMessage));
    }

    [Test]
    public async Task TypedSessionProtocolDial_ExceptionPropagatesToCaller()
    {
        await using ErrorHandlingE2eTestSetup test = new();
        await test.AddPeersAsync(2);

        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        ExpectedProtocolException? exception = Assert.ThrowsAsync<ExpectedProtocolException>(
            async () => await session.DialAsync<ThrowingTypedSessionProtocol, string, string>("request"));

        Assert.That(exception?.Message, Is.EqualTo(ThrowingTypedSessionProtocol.ErrorMessage));
    }

    [Test]
    public async Task TransportFailure_ThrowsTraceablePeerConnectionException()
    {
        ChannelBus channelBus = new();
        await using ServiceProvider peerAProvider = MakeServiceProvider(channelBus);
        await using ILocalPeer peerA = peerAProvider.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));

        await peerA.StartListenAsync();

        string expectedRemoteAddress = TestPeers.Multiaddr(2).ToString();

        PeerConnectionException? exception = Assert.ThrowsAsync<PeerConnectionException>(
            async () => await peerA.DialAsync(TestPeers.Multiaddr(2)));

        Assert.Multiple(() =>
        {
            Assert.That(exception?.SessionId, Is.Not.Null.And.Not.Empty);
            Assert.That(exception?.LocalPeerId, Is.EqualTo(peerA.Identity.PeerId.ToString()));
            Assert.That(exception?.RemoteAddress, Is.EqualTo(expectedRemoteAddress));
            Assert.That(exception?.Message, Does.Contain(exception!.SessionId));
            Assert.That(exception?.Message, Does.Contain(expectedRemoteAddress));
            Assert.That(exception?.InnerException, Is.Not.Null);
        });
    }

    private static ServiceProvider MakeServiceProvider(ChannelBus channelBus)
    {
        return new ServiceCollection()
            .AddSingleton<IPeerFactoryBuilder>(sp =>
            {
                TestBuilder builder = new(sp);
                builder.AddProtocol<ThrowingSessionProtocol>();
                builder.AddProtocol<ThrowingTypedSessionProtocol>();
                return builder;
            })
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton<PeerStore>()
            .AddSingleton(channelBus)
            .AddSingleton(sp => sp.GetRequiredService<IPeerFactoryBuilder>().Build())
            .BuildServiceProvider();
    }

    private sealed class ErrorHandlingE2eTestSetup : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index)
            => [$"/ip4/127.0.0.1/tcp/0"];

        protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
        {
            builder.AddProtocol<ThrowingSessionProtocol>();
            builder.AddProtocol<ThrowingTypedSessionProtocol>();
            return builder;
        }
    }

    private sealed class ThrowingSessionProtocol : ISessionProtocol
    {
        public const string ErrorMessage = "session protocol dial failed";

        public string Id => "throwing-session-protocol";

        public Task DialAsync(IChannel downChannel, ISessionContext context)
            => Task.FromException(new ExpectedProtocolException(ErrorMessage));

        public Task ListenAsync(IChannel downChannel, ISessionContext context)
            => Task.CompletedTask;
    }

    private sealed class ThrowingTypedSessionProtocol : ISessionProtocol<string, string>
    {
        public const string ErrorMessage = "typed session protocol dial failed";

        public string Id => "throwing-typed-session-protocol";

        public Task<string> DialAsync(IChannel downChannel, ISessionContext context, string request)
            => Task.FromException<string>(new ExpectedProtocolException(ErrorMessage));

        public Task ListenAsync(IChannel downChannel, ISessionContext context)
            => Task.CompletedTask;
    }

    private sealed class ExpectedProtocolException(string message) : Exception(message);
}
