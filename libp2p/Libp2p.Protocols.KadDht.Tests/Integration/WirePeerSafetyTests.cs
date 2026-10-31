// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class WirePeerSafetyTests
{
    internal static PeerId Id(int index) => new([0x12, 0x20, .. System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index))]);

    internal static Message.Types.Peer WirePeer(int index, params string[] addresses)
    {
        var peer = new Message.Types.Peer { Id = ByteString.CopyFrom(Id(index).Bytes) };
        foreach (var address in addresses)
            peer.Addrs.Add(ByteString.CopyFrom(Multiaddress.Decode(address).ToBytes()));
        return peer;
    }

    [TestCase(Message.Types.MessageType.FindNode)]
    [TestCase(Message.Types.MessageType.GetValue)]
    [TestCase(Message.Types.MessageType.GetProviders)]
    public async Task RegisteredSender_BoundsDiscoveryAndPreservesUnrelatedPeers(Message.Types.MessageType type)
    {
        var services = new ServiceCollection();
        var local = Substitute.For<ILocalPeer>();
        var store = new PeerStore(100);
        store.Discover([Multiaddress.Decode($"/ip4/1.1.1.1/p2p/{Id(-1)}")], isTrusted: false);
        services.AddSingleton(local);
        services.AddSingleton(store);
        services.AddKadDht();
        using var provider = services.BuildServiceProvider();
        var response = new Message { Type = type };
        for (int i = 0; i < 3000; i++) response.CloserPeers.Add(WirePeer(i, "/ip4/127.0.0.1/tcp/4001"));
        var session = Substitute.For<ISession>();
        local.DialAsync(Id(-2), Arg.Any<CancellationToken>()).Returns(session);
        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(response);
        var sender = provider.GetRequiredService<IDhtMessageSender>();
        var receiver = Id(-2).ToDhtNode();
        DhtNode[]? peers = type switch
        {
            Message.Types.MessageType.FindNode => await sender.FindNeighbours(receiver, new PublicKey([1]), default),
            Message.Types.MessageType.GetValue => (await sender.GetValueAsync(receiver, [1])).CloserPeers,
            _ => (await sender.GetProvidersAsync(receiver, [1])).CloserPeers
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers!.Select(p => p.PeerId), Is.EqualTo(Enumerable.Range(0, 20).Select(Id)));
            Assert.That(store.TryGetPeerInfo(Id(-1), out _), Is.True);
            Assert.That(store.TryGetPeerInfo(Id(20), out _), Is.False);
        }
    }

    [TestCase("services")]
    [TestCase("fallback")]
    [TestCase("integration")]
    [TestCase("direct")]
    public async Task RegisteredHandler_EchoesOnlyStoredValues(string path)
    {
        using var harness = new KadWireHarness(path, new KadDhtOptions { MaxStoredValues = 1 });
        var first = await harness.SendAsync(MessageHelper.CreatePutValueRequest([1], [10]));
        var second = await harness.SendAsync(MessageHelper.CreatePutValueRequest([2], [20]));
        Assert.That(first.Record?.Value, Is.EqualTo(ByteString.CopyFrom(new byte[] { 10 })));
        Assert.That(second.Record, Is.Null);
        Assert.That((await harness.SendAsync(MessageHelper.CreateGetValueRequest([1]))).Record?.Value, Is.EqualTo(first.Record!.Value));
        Assert.That((await harness.SendAsync(MessageHelper.CreateGetValueRequest([2]))).Record, Is.Null);
    }

    [TestCase("")]
    [TestCase("FFFFFF")]
    [TestCase("122000")]
    [TestCase("12010000")]
    [TestCase("92000100")]
    public void Converter_RejectsMalformedIdentity(string hex)
    {
        var peer = new Message.Types.Peer { Id = ByteString.CopyFrom(Convert.FromHexString(hex)) };
        Assert.That(MessageHelper.FromWirePeer(peer), Is.Null);
    }

    [Test]
    public void Serialization_WithoutEvidenceIsNotConnected() =>
        Assert.That(MessageHelper.ToWirePeer(Id(1).ToDhtNode()).Connection, Is.EqualTo(Message.Types.ConnectionType.NotConnected));

    [TestCase(false)]
    [TestCase(true)]
    public async Task ValueStore_ReportsWhetherRequestedBytesWereRetained(bool identical)
    {
        var store = new InMemoryValueStore();
        await store.PutValueAsync(new byte[] { 1 }, new StoredValue { Value = [10], Timestamp = 100, Ttl = TimeSpan.FromHours(1) });
        bool result = await store.PutValueAsync(new byte[] { 1 }, new StoredValue { Value = identical ? [10] : [20], Timestamp = 99, Ttl = TimeSpan.FromHours(1) });
        Assert.That(result, Is.EqualTo(identical));
        Assert.That((await store.GetValueAsync(new byte[] { 1 }))!.Value, Is.EqualTo(new byte[] { 10 }));
    }
}

internal sealed class KadWireHarness : IDisposable
{
    private readonly ServiceProvider _services;
    internal RequestResponseProtocol<Message, Message> Protocol { get; }
    internal ISessionContext Context { get; }
    internal SharedDhtState? RoutingState { get; }

    internal KadWireHarness(string path, KadDhtOptions options, IValueStore? values = null,
        IProviderStore? providers = null, Func<PublicKey, IEnumerable<DhtNode>>? nearest = null, ILocalPeer? local = null,
        ILoggerFactory? loggerFactory = null, SharedDhtState? sharedState = null)
    {
        var services = new ServiceCollection();
        if (path == "services") services.AddKadDht(o => CopyOptions(options, o));
        else services.AddSingleton(options);
        if (values is not null) services.AddSingleton(values);
        if (providers is not null) services.AddSingleton(providers);
        if (local is not null) services.AddSingleton(local);
        if (loggerFactory is not null) services.AddSingleton(loggerFactory);
        if (sharedState is not null) services.AddSingleton(sharedState);
        _services = services.BuildServiceProvider();
        var builder = Substitute.For<ILibp2pPeerFactoryBuilder>();
        builder.Services.Returns(services);
        builder.ServiceProvider.Returns(_services);
        RequestResponseProtocol<Message, Message>? protocol = null;
        builder.AddProtocol(Arg.Any<RequestResponseProtocol<Message, Message>>(), Arg.Any<bool>(), Arg.Any<InboundStreamLimit>())
            .Returns(call => { protocol = call.Arg<RequestResponseProtocol<Message, Message>>(); return builder; });
        builder.AddProtocol<KadDhtProtocol>().Returns(builder);
        switch (path)
        {
            case "services":
            case "fallback": builder.WithKadDht(); break;
            case "integration": builder.AddKadDht(o => CopyOptions(options, o), valueStore: values, providerStore: providers); break;
            case "direct": builder.AddKadDhtProtocols(nearest ?? (_ => []), options: options, valueStore: values, providerStore: providers); break;
            default: throw new ArgumentException("Unknown registration path", nameof(path));
        }
        RoutingState = path == "integration"
            ? services.Last(service => service.ServiceType == typeof(SharedDhtState)).ImplementationInstance as SharedDhtState
            : _services.GetService<SharedDhtState>();
        Protocol = protocol!;
        Context = Substitute.For<ISessionContext>();
        Context.State.Returns(new State());
        if (local is not null) Context.Peer.Returns(local);
    }

    internal async Task<Message> SendAsync(Message request)
    {
        var channel = new Channel();
        TaskCompletionSource requestWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A core Channel can close before the sender resumes from its final write acknowledgement.
        // Keep one-way listener teardown behind that acknowledgement so this harness tests the
        // registered handler without racing the separate client-write/transport-close contract.
        IChannel listenerChannel = request.Type == Message.Types.MessageType.AddProvider
            ? new GatedCloseChannel(channel.Reverse, requestWritten.Task) : channel.Reverse;
        var listen = Protocol.ListenAsync(listenerChannel, Context);
        try
        {
            var eof = request.Type == Message.Types.MessageType.AddProvider
                ? channel.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask() : null;
            var response = await Protocol.DialAsync(channel, Context, request).WaitAsync(TimeSpan.FromSeconds(5));
            requestWritten.TrySetResult();
            if (eof is not null) await eof.WaitAsync(TimeSpan.FromSeconds(5));
            await listen.WaitAsync(TimeSpan.FromSeconds(5));
            return response;
        }
        finally
        {
            requestWritten.TrySetResult();
            await channel.CloseAsync();
            await listen.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class GatedCloseChannel(IChannel inner, Task requestWritten) : IChannel
    {
        public async ValueTask CloseAsync() { await requestWritten; await inner.CloseAsync(); }
        public ValueTask AbortAsync() => inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default) => inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
    }

    private static void CopyOptions(KadDhtOptions source, KadDhtOptions target)
    {
        foreach (var property in typeof(KadDhtOptions).GetProperties().Where(p => p.CanWrite))
            property.SetValue(target, property.GetValue(source));
    }

    public void Dispose() => _services.Dispose();
}
