// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;
using System.Collections.Concurrent;

namespace Libp2p.E2eTests;

public class MplexNegotiationTests
{
    [TestCase(0)]
    [TestCase(1)]
    public async Task FallbackNegotiatesMplexWithMplexOnlyPeer(int dialer)
    {
        await using NegotiationSetup setup = new(bothOfferYamux: false);
        await setup.AddPeersAsync(2);

        await ConnectAndPingAsync(setup, dialer, "/mplex/6.7.0");
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task BothOfferedPreferYamux(int dialer)
    {
        await using NegotiationSetup setup = new(bothOfferYamux: true);
        await setup.AddPeersAsync(2);

        await ConnectAndPingAsync(setup, dialer, "/yamux/1.0.0");
    }

    private static async Task ConnectAndPingAsync(NegotiationSetup setup, int dialer, string expectedMuxer)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        int listener = 1 - dialer;
        TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Peers[listener].OnConnected += session => accepted.TrySetResult(session);

        ISession outbound = await setup.Peers[dialer].DialAsync([.. setup.Peers[listener].ListenAddresses], deadline.Token)
            .WaitAsync(deadline.Token);
        ISession inbound = await accepted.Task.WaitAsync(deadline.Token);

        await Task.WhenAll(outbound.DialAsync<PingProtocol>(deadline.Token), inbound.DialAsync<PingProtocol>(deadline.Token))
            .WaitAsync(deadline.Token);

        Assert.Multiple(() =>
        {
            Assert.That(outbound.RemotePeerId, Is.EqualTo(setup.Peers[listener].Identity.PeerId));
            Assert.That(inbound.RemotePeerId, Is.EqualTo(setup.Peers[dialer].Identity.PeerId));
            Assert.That(setup.ServiceProviders[dialer].GetRequiredService<MuxerObservation>().Connections,
                Is.EqualTo(new[] { (expectedMuxer, false) }));
            Assert.That(setup.ServiceProviders[listener].GetRequiredService<MuxerObservation>().Connections,
                Is.EqualTo(new[] { (expectedMuxer, true) }));
        });
    }

    private sealed class NegotiationSetup(bool bothOfferYamux) : E2eTestSetup
    {
        private int _configuredPeers;

        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];

        protected override IServiceCollection ConfigureServices(IServiceCollection services)
        {
            bool mplexOnly = !bothOfferYamux && _configuredPeers == 1;
            _configuredPeers++;
            return base.ConfigureServices(services)
                .AddSingleton(new MuxerObservation())
                .AddSingleton<IPeerFactoryBuilder>(provider => new NegotiationBuilder(provider, mplexOnly));
        }
    }

    private sealed class NegotiationBuilder(IServiceProvider provider, bool mplexOnly) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] StreamTransports() => [Get<IpTcpProtocol>()];
        protected override ProtocolRef[] Encryption() => [Get<NoiseProtocol>()];
        protected override ProtocolRef[] Muxers() => mplexOnly
            ? [Get<RecordingMplexProtocol>()]
            : [Get<RecordingYamuxProtocol>(), Get<RecordingMplexProtocol>()];
    }

    private sealed class MuxerObservation
    {
        public ConcurrentQueue<(string Protocol, bool IsListener)> Connections { get; } = new();
    }

    private sealed class RecordingMplexProtocol(MuxerObservation observation, MultiplexerSettings? settings = null,
        ILoggerFactory? loggerFactory = null) : MplexProtocol(settings, loggerFactory)
    {
        protected override Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
        {
            observation.Connections.Enqueue((Id, isListener));
            return base.ConnectAsync(channel, context, isListener);
        }
    }

    private sealed class RecordingYamuxProtocol(MuxerObservation observation, MultiplexerSettings? settings = null,
        ILoggerFactory? loggerFactory = null) : YamuxProtocol(settings, loggerFactory)
    {
        protected override Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
        {
            observation.Connections.Enqueue((Id, isListener));
            return base.ConnectAsync(channel, context, isListener);
        }
    }
}
