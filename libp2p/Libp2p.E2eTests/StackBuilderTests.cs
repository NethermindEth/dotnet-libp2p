// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class StackBuilderTests
{
    private const string Selector = "/multistream/1.0.0";
    private static readonly string[] CoreProtocols = ["/ipfs/id/1.0.0", "/ipfs/id/push/1.0.0", "/ipfs/ping/1.0.0"];
    private static readonly string[] RelayProtocols = ["/libp2p/circuit/relay/0.2.0/stop", "/libp2p/circuit/relay/0.2.0/hop"];
    private static readonly string[] PubsubProtocols = ["/meshsub/1.3.0", "/meshsub/1.2.0", "/meshsub/1.1.0", "/meshsub/1.0.0", "/floodsub/1.0.0"];
    private static IEnumerable<int> StackVariants => Enumerable.Range(0, 64);

    [TestCase("/ipfs/id/1.0.0", false)]
    [TestCase("/ipfs/id/1.0.0", true)]
    [TestCase("/ipfs/id/push/1.0.0", false)]
    [TestCase("/ipfs/id/push/1.0.0", true)]
    [TestCase("/ipfs/ping/1.0.0", false)]
    [TestCase("/ipfs/ping/1.0.0", true)]
    public void OneCoreProtocolCanBeReplacedOrOmitted(string id, bool omit)
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        CoreBuilder builder = new(provider, id, omit);
        builder.AddProtocol(new NamedProtocol("/extra"));
        builder.Build();
        IProtocolStackSettings stack = provider.GetRequiredService<IProtocolStackSettings>();
        ProtocolRef[] transports = stack.TopProtocols!;
        ProtocolRef[] apps = FollowStream(stack, [transports.Single()], ["ip-tcp"], ["/noise", "/tls/1.0.0"]);
        string[] expected = [.. CoreProtocols.Where(p => !omit || p != id).Select(p => p == id ? "/replacement" : p), "/extra"];
        Assert.That(apps.Select(p => p.Id), Is.EqualTo(expected));
    }

    [Test]
    public void LayerOverridesInheritTheStandardWiring()
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        LayerBuilder builder = new(provider);
        builder.WithWebSockets();
        builder.WithWebRtcDirect();
        builder.WithPubsub();
        builder.AddProtocol(new NamedProtocol("/extra"));
        builder.Build();
        IProtocolStackSettings stack = provider.GetRequiredService<IProtocolStackSettings>();
        ProtocolRef[] transports = stack.TopProtocols!;
        Assert.That(transports.Select(p => p.Id), Is.EqualTo(new[] { "ip-tcp", "quic-v1" }));
        ProtocolRef[] apps = FollowStream(stack, [transports[0]], ["ip-tcp"], ["/plaintext/2.0.0"]);
        Assert.That(stack.Protocols!.Keys.Single(p => p.Id == "/yamux/1.0.0").Protocol, Is.SameAs(builder.Muxer));
        Assert.That(apps.Select(p => p.Id), Is.EqualTo(CoreProtocols.Concat(["/extra", RelayProtocols[0], "/floodsub/1.0.0"])));
        ProtocolRef selector = stack.Protocols[transports[1]].Single();
        Assert.That(stack.Protocols[selector], Is.EqualTo(apps));
        ProtocolRef relay = apps.Single(p => p.Id == RelayProtocols[0]);
        Assert.That(Next(stack, stack.Protocols[relay], Selector), Is.EqualTo(apps.Where(p => p != relay)));
    }

    [Test]
    public void StreamLayerCanBeOmittedForDirectTransport()
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p<DirectBuilder>().BuildServiceProvider();
        _ = provider.GetRequiredService<IPeerFactory>();
        IProtocolStackSettings stack = provider.GetRequiredService<IProtocolStackSettings>();
        ProtocolRef[] transports = stack.TopProtocols!;
        Assert.That(transports.Select(p => p.Id), Is.EqualTo(new[] { "quic-v1" }));
        ProtocolRef[] apps = Next(stack, Next(stack, transports, "quic-v1"), Selector);
        Assert.That(apps.Select(p => p.Id), Is.EqualTo(CoreProtocols));
    }

    [TestCaseSource(nameof(StackVariants))]
    public void StandardStackPreservesAllOptionCombinations(int flags)
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        Libp2pPeerFactoryBuilder builder = (Libp2pPeerFactoryBuilder)provider.GetRequiredService<IPeerFactoryBuilder>();
        if ((flags & 1) != 0) builder.WithPlaintextEnforced();
        if ((flags & 2) != 0) builder.WithRelay();
        if ((flags & 4) != 0) builder.WithPubsub();
        if ((flags & 8) != 0) builder.WithQuic();
        if ((flags & 16) != 0) builder.WithWebSockets();
        if ((flags & 32) != 0) builder.WithWebRtcDirect();
        builder.AddProtocol(new NamedProtocol("/extra"));
        builder.Build();
        IProtocolStackSettings stack = provider.GetRequiredService<IProtocolStackSettings>();
        ProtocolRef[] transports = stack.TopProtocols!;
        string[] streams = (flags & 16) != 0 ? ["ip-tcp", "websocket"] : ["ip-tcp"];
        string[] datagrams = [.. ((flags & 8) != 0 ? new[] { "quic-v1" } : []), .. ((flags & 32) != 0 ? new[] { "webrtc-direct" } : [])];
        Assert.That(transports.Select(p => p.Id), Is.EqualTo(streams.Concat(datagrams)));

        ProtocolRef[] layer = Next(stack, transports.Take(streams.Length).ToArray(), streams);
        layer = Next(stack, layer, Selector);
        layer = Next(stack, layer, (flags & 1) != 0 ? ["/plaintext/2.0.0"] : ["/noise", "/tls/1.0.0"]);
        layer = Next(stack, layer, Selector);
        layer = Next(stack, layer, "/yamux/1.0.0");
        ProtocolRef[] selector = layer;
        ProtocolRef[] apps = Next(stack, layer, Selector);
        string[] expectedApps = [.. CoreProtocols, "/extra", .. ((flags & 2) != 0 ? RelayProtocols : []), .. ((flags & 4) != 0 ? PubsubProtocols : [])];
        Assert.That(apps.Select(p => p.Id), Is.EqualTo(expectedApps));
        foreach (ProtocolRef datagram in transports.Skip(streams.Length))
        {
            Assert.That(stack.Protocols![datagram], Is.EqualTo(selector));
        }
        ProtocolRef[] relay = apps.Where(p => RelayProtocols.Contains(p.Id)).ToArray();
        if (relay.Length > 0)
        {
            ProtocolRef[] relaySelector = Next(stack, relay, RelayProtocols);
            Assert.That(Next(stack, relaySelector, Selector), Is.EqualTo(apps.Except(relay).ToArray()));
        }
    }

    private static ProtocolRef[] Next(IProtocolStackSettings stack, ProtocolRef[] layer, params string[] ids)
    {
        Assert.That(layer.Select(p => p.Id), Is.EqualTo(ids));
        ProtocolRef[] next = stack.Protocols![layer[0]];
        foreach (ProtocolRef protocol in layer) Assert.That(stack.Protocols[protocol], Is.EqualTo(next));
        return next;
    }

    private static ProtocolRef[] FollowStream(IProtocolStackSettings stack, ProtocolRef[] streams, string[] streamIds, string[] encryption)
    {
        ProtocolRef[] layer = Next(stack, streams, streamIds);
        layer = Next(stack, layer, Selector);
        layer = Next(stack, layer, encryption);
        layer = Next(stack, layer, Selector);
        layer = Next(stack, layer, "/yamux/1.0.0");
        return Next(stack, layer, Selector);
    }

    private sealed class CoreBuilder(IServiceProvider provider, string id, bool omit) : Libp2pPeerFactoryBuilder(provider)
    {
        private ProtocolRef? Replacement => omit ? null : new(new NamedProtocol("/replacement"));
        protected override ProtocolRef? Identify() => id == CoreProtocols[0] ? Replacement : base.Identify();
        protected override ProtocolRef? IdentifyPush() => id == CoreProtocols[1] ? Replacement : base.IdentifyPush();
        protected override ProtocolRef? Ping() => id == CoreProtocols[2] ? Replacement : base.Ping();
    }

    private sealed class LayerBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        public YamuxProtocol Muxer { get; } = new();
        protected override ProtocolRef[] StreamTransports() => [Get<IpTcpProtocol>()];
        protected override ProtocolRef[] DatagramTransports() => [Get<QuicProtocol>()];
        protected override ProtocolRef[] Encryption() => [Get<PlainTextProtocol>()];
        protected override ProtocolRef[] Muxers() => [new(Muxer)];
        protected override ProtocolRef[] Relay() => [Get<RelayStopProtocol>()];
        protected override ProtocolRef[] Pubsub() => [Get<FloodsubProtocol>()];
    }

    private sealed class DirectBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] StreamTransports() => [];
        protected override ProtocolRef[] DatagramTransports() => [Get<QuicProtocol>()];
    }

    private sealed class NamedProtocol(string id) : ISessionProtocol
    {
        public string Id => id;
        public Task DialAsync(IChannel downChannel, ISessionContext context) => Task.CompletedTask;
        public Task ListenAsync(IChannel downChannel, ISessionContext context) => Task.CompletedTask;
    }
}
