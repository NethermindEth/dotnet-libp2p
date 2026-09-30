// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class ProviderAnnouncementTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Provide_WithoutResponse_SucceedsAndRepublishes(bool remoteCloses)
    {
        var localPeer = Substitute.For<ILocalPeer>();
        var identity = new Identity(new byte[32]);
        localPeer.Identity.Returns(identity);
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>
        {
            Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{identity.PeerId}")
        });
        var remotePeer = new Identity(Enumerable.Repeat((byte)1, 32).ToArray()).PeerId;
        var session = Substitute.For<ISession>();
        localPeer.DialAsync(remotePeer, Arg.Any<CancellationToken>()).Returns(session);
        var providerStore = new InMemoryProviderStore(20);
        var wireProtocol = CreateWireProtocol(providerStore);
        var context = CreateContext(identity.PeerId);
        var republished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int announcements = 0;

        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var channel = new Channel();
            var receive = ReceiveAsync(channel.Reverse);
            try
            {
                return await wireProtocol.DialAsync(channel, context, call.Arg<Message>())
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await channel.CloseAsync();
                await receive;
            }
        });

        async Task ReceiveAsync(IChannel channel)
        {
            var message = await channel.ReadPrefixedProtobufAsync(Message.Parser);
            if (message.Type == Message.Types.MessageType.AddProvider)
            {
                var provider = MessageHelper.FromWirePeer(message.ProviderPeers.Single())!;
                await providerStore.AddProviderAsync(message.Key.ToByteArray(), new ProviderRecord
                {
                    PeerId = provider.PeerId,
                    Multiaddrs = provider.Multiaddrs,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ttl = TimeSpan.FromHours(1)
                });
                if (Interlocked.Increment(ref announcements) == 2) republished.TrySetResult();
                if (remoteCloses) await channel.CloseAsync();
            }
            else
            {
                await channel.WriteSizeAndProtobufAsync(new Message { Type = message.Type });
            }
        }

        var sender = new LibP2pKademliaMessageSender(localPeer);
        using var protocol = new KadDhtProtocol(localPeer, sender, sender,
            new KadDhtOptions { Mode = KadDhtMode.Client, ProviderRepublishInterval = TimeSpan.FromMilliseconds(20) },
            new InMemoryValueStore(20), new InMemoryProviderStore(20));
        protocol.AddNode(remotePeer.ToDhtNode());
        byte[] key = [1, 2, 3];

        Assert.That(await protocol.ProvideAsync(key), Is.True);
        Assert.That((await providerStore.GetProvidersAsync(key, 20)).Single().PeerId, Is.EqualTo(identity.PeerId));

        using var stop = new CancellationTokenSource();
        var run = protocol.RunAsync(stop.Token);
        try
        {
            await republished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public async Task Listen_AddProvider_StoresWithoutWritingResponse()
    {
        var peerId = new Identity(new byte[32]).PeerId;
        var store = new InMemoryProviderStore(20);
        var protocol = CreateWireProtocol(store);
        var channel = new Channel();
        var listen = protocol.ListenAsync(channel.Reverse, CreateContext(peerId));
        byte[] key = [1, 2, 3];
        try
        {
            var read = channel.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask();
            await ((IChannel)channel).WriteSizeAndProtobufAsync(MessageHelper.CreateAddProviderRequest(key, [peerId.ToDhtNode()]));
            var response = await read.WaitAsync(TimeSpan.FromSeconds(2));
            await listen.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(response.Result, Is.EqualTo(IOResult.Ended));
            Assert.That((await store.GetProvidersAsync(key, 20)).Single().PeerId, Is.EqualTo(peerId));
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddProvider_PropagatesDialAndWriteFailures(bool failWrite)
    {
        var localPeer = Substitute.For<ILocalPeer>();
        var peerId = new Identity(new byte[32]).PeerId;
        var session = Substitute.For<ISession>();
        var channel = new Channel();
        await channel.CloseAsync();
        var wireProtocol = CreateWireProtocol(new InMemoryProviderStore(20));
        localPeer.DialAsync(peerId, Arg.Any<CancellationToken>()).Returns(_ => failWrite
            ? Task.FromResult(session)
            : Task.FromException<ISession>(new InvalidOperationException("Dial failed")));
        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(call =>
                wireProtocol.DialAsync(channel, CreateContext(peerId), call.Arg<Message>()));
        var sender = new LibP2pKademliaMessageSender(localPeer);

        if (failWrite)
            Assert.ThrowsAsync<ChannelClosedException>(() => sender.AddProviderAsync(peerId.ToDhtNode(), [1], peerId.ToDhtNode()));
        else
            Assert.ThrowsAsync<InvalidOperationException>(() => sender.AddProviderAsync(peerId.ToDhtNode(), [1], peerId.ToDhtNode()));
    }

    private static ISessionContext CreateContext(PeerId peerId)
    {
        var context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = Multiaddress.Decode($"/p2p/{peerId}") });
        return context;
    }

    private static RequestResponseProtocol<Message, Message> CreateWireProtocol(IProviderStore store)
    {
        var builder = Substitute.For<IPeerFactoryBuilder>();
        using var services = new ServiceCollection().BuildServiceProvider();
        builder.ServiceProvider.Returns(services);
        RequestResponseProtocol<Message, Message>? protocol = null;
        builder.AddProtocol(Arg.Any<RequestResponseProtocol<Message, Message>>(), Arg.Any<bool>())
            .Returns(call => { protocol = call.Arg<RequestResponseProtocol<Message, Message>>(); return builder; });
        builder.AddKadDhtProtocols(_ => [], providerStore: store);
        return protocol!;
    }
}
