// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class KadStreamRegistrationTests
{
    [TestCase("direct", false)]
    [TestCase("direct", true)]
    [TestCase("integration", false)]
    [TestCase("integration", true)]
    [TestCase("services", false)]
    [TestCase("services", true)]
    public async Task CustomBuildersReceiveLimitsAndConfiguredDeadline(string path, bool client)
    {
        ManualTimeProvider clock = new();
        void Configure(KadDhtOptions options)
        {
            options.ProtocolId = "/test/kad/1";
            options.ReadTimeout = TimeSpan.FromSeconds(7);
            options.OperationTimeout = TimeSpan.FromHours(1);
            options.MaxInboundStreams = 3;
            options.MaxInboundStreamsPerPeer = 2;
            options.Mode = client ? KadDhtMode.Client : KadDhtMode.Server;
        }
        var options = new KadDhtOptions();
        Configure(options);
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock);
        if (path == "services") services.AddKadDht(Configure);
        else services.AddSingleton(options);
        using var provider = services.BuildServiceProvider();
        var builder = Substitute.For<ILibp2pPeerFactoryBuilder>();
        builder.ServiceProvider.Returns(provider);
        builder.Services.Returns(services);
        RequestResponseProtocol<Message, Message>? protocol = null;
        InboundStreamLimit? limit = null;
        builder.AddProtocol(Arg.Any<RequestResponseProtocol<Message, Message>>(), Arg.Any<bool>(), Arg.Any<InboundStreamLimit>())
            .Returns(call =>
            {
                protocol = call.Arg<RequestResponseProtocol<Message, Message>>();
                limit = call.Arg<InboundStreamLimit>();
                return builder;
            });
        switch (path)
        {
            case "direct": builder.AddKadDhtProtocols(_ => [], baseId: options.ProtocolId, options: options); break;
            case "integration": builder.AddKadDht(Configure); break;
            case "services": builder.WithKadDht(); break;
        }
        Assert.That(protocol, Is.Not.Null);
        Assert.That(protocol!.Id, Is.EqualTo(options.ProtocolId));
        Assert.That(protocol.ReadTimeout, Is.EqualTo(options.ReadTimeout));
        Assert.That(limit, Is.Not.Null);
        Assert.That(limit!.MaxConcurrent, Is.EqualTo(3));
        Assert.That(limit.MaxPerPeer, Is.EqualTo(2));
        var context = Substitute.For<ISessionContext>();
        context.State.Returns(new State());
        Channel channel = new();
        Task listen = protocol.ListenAsync(channel, context);
        try
        {
            Assert.That(clock.DueTimes, Is.EqualTo(new[] { options.ReadTimeout }));
            clock.Advance(options.ReadTimeout);
            Assert.ThrowsAsync<ChannelClosedException>(async () => await listen.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
        }
        finally
        {
            await channel.AbortAsync();
            await listen.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("total-zero")]
    [TestCase("total-negative")]
    [TestCase("peer-zero")]
    [TestCase("peer-negative")]
    [TestCase("timeout-zero")]
    [TestCase("timeout-infinite")]
    [TestCase("timeout-too-long")]
    public void InvalidConfigurationIsRejectedAtRegistration(string invalid)
    {
        void Configure(KadDhtOptions options)
        {
            switch (invalid)
            {
                case "total-zero": options.MaxInboundStreams = 0; break;
                case "total-negative": options.MaxInboundStreams = -1; break;
                case "peer-zero": options.MaxInboundStreamsPerPeer = 0; break;
                case "peer-negative": options.MaxInboundStreamsPerPeer = -1; break;
                case "timeout-zero": options.ReadTimeout = TimeSpan.Zero; break;
                case "timeout-infinite": options.ReadTimeout = Timeout.InfiniteTimeSpan; break;
                case "timeout-too-long": options.ReadTimeout = TimeSpan.FromMilliseconds(uint.MaxValue); break;
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddKadDht(Configure));
        var builder = Substitute.For<IPeerFactoryBuilder>();
        var options = new KadDhtOptions();
        Configure(options);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddKadDhtProtocols(_ => [], options: options));
    }

    [Test]
    public void OldBuilderCannotSilentlyDropTheLimit()
    {
        var builder = new OldBuilder();
        Assert.Throws<NotSupportedException>(() => builder.AddKadDhtProtocols(_ => []));
        Assert.That(builder.Registered, Is.False);
    }

    private sealed class OldBuilder : IPeerFactoryBuilder
    {
        public bool Registered { get; private set; }
        public IServiceProvider ServiceProvider { get; } = Substitute.For<IServiceProvider>();
        public IPeerFactoryBuilder AddProtocol<TProtocol>(TProtocol? instance, bool isExposed) where TProtocol : IProtocol
        {
            Registered = true;
            return this;
        }
        public IPeerFactory Build() => throw new NotSupportedException();
    }
}
