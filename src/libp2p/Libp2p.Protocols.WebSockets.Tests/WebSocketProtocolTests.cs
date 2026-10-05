// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.PlainText.Dto;
using System.Diagnostics;

namespace Libp2p.Protocols.WebSockets.Tests;

public class WebSocketProtocolTests
{
    [Test]
    public void IsAddressMatchOnlyMatchesWebSocketAddresses()
    {
        Multiaddress tcp = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001");
        Multiaddress ws = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001/ws");
        Multiaddress wss = Multiaddress.Decode("/dns/example.com/tcp/443/wss");

        Assert.That(WebSocketProtocol.IsAddressMatch(ws), Is.True);
        Assert.That(WebSocketProtocol.IsAddressMatch(wss), Is.True);
        Assert.That(WebSocketProtocol.IsAddressMatch(tcp), Is.False);

        Assert.That(IpTcpProtocol.IsAddressMatch(tcp), Is.True);
        Assert.That(IpTcpProtocol.IsAddressMatch(ws), Is.False);
        Assert.That(IpTcpProtocol.IsAddressMatch(wss), Is.False);
    }

    [Test]
    public async Task PeersExchangeDataOverWebSocketTransport()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder
                .WithWebSockets()
                .AddProtocol<IncrementNumberProtocol>())
            .BuildServiceProvider();

        IPeerFactory peerFactory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer listener = peerFactory.Create();
        await using ILocalPeer dialer = peerFactory.Create();

        await listener.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0/ws")]);
        Multiaddress listenAddress = await WaitForListenAddressAsync(listener);
        Assert.That(listenAddress.Get<TCP>().ToString(), Is.Not.EqualTo("0"));

        ISession session = await dialer.DialAsync(listenAddress);
        int response = await session.DialAsync<IncrementNumberProtocol, int, int>(41);

        Assert.That(response, Is.EqualTo(42));
    }

    [Test]
    public async Task PeersExchangeDataOverPlaintextWebSocketTransport()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder
                .WithWebSockets()
                .WithPlaintextEnforced()
                .AddProtocol<IncrementNumberProtocol>())
            .BuildServiceProvider();

        IPeerFactory peerFactory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer listener = peerFactory.Create();
        await using ILocalPeer dialer = peerFactory.Create();

        await listener.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0/ws")]);
        Multiaddress listenAddress = await WaitForListenAddressAsync(listener);

        ISession session = await dialer.DialAsync(listenAddress);
        int response = await session.DialAsync<IncrementNumberProtocol, int, int>(41);

        Assert.That(response, Is.EqualTo(42));
    }

    [Test]
    public async Task PlaintextProtocol_RejectsRemotePublicKeyThatDoesNotMatchAddressPeerId()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder
                .WithWebSockets()
                .WithPlaintextEnforced()
                .AddProtocol<IncrementNumberProtocol>())
            .BuildServiceProvider();

        IPeerFactory peerFactory = services.GetRequiredService<IPeerFactory>();
        Identity listenerIdentity = new();
        Identity dialerIdentity = new();
        Identity wrongIdentity = new();
        await using ILocalPeer dialer = peerFactory.Create(dialerIdentity);
        Channel channel = new();
        PlainTextProtocol protocol = new();

        TestConnectionContext dialerContext = new(dialer, Multiaddress.Decode($"/p2p/{wrongIdentity.PeerId}"));
        Task replyTask = Task.Run(() => ReplyWithPlaintextIdentityAsync(channel, listenerIdentity));

        Libp2pException? exception = Assert.ThrowsAsync<Libp2pException>(async () =>
            await protocol.DialAsync(channel.Reverse, dialerContext).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.That(exception?.Message, Does.Contain("does not match address peer id"));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(2));
        await channel.CloseAsync();
    }

    private static async Task<Multiaddress> WaitForListenAddressAsync(ILocalPeer peer)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Multiaddress? address = peer.ListenAddresses.FirstOrDefault();
            if (address is not null)
            {
                return address;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for listener multiaddress.");
        return null!;
    }

    private sealed class IncrementNumberProtocol : ISessionProtocol<int, int>
    {
        public string Id => "/test/increment/1.0.0";

        public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, int request)
        {
            await downChannel.WriteVarintAsync(request);
            return await downChannel.ReadVarintAsync();
        }

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            int request = await downChannel.ReadVarintAsync();
            await downChannel.WriteVarintAsync(request + 1);
        }
    }

    private static async Task ReplyWithPlaintextIdentityAsync(IChannel channel, Identity identity)
    {
        int structSize = await channel.ReadVarintAsync();
        _ = await channel.ReadAsync(structSize).OrThrow();
        Exchange response = new()
        {
            Id = ByteString.CopyFrom(identity.PeerId.Bytes),
            Pubkey = identity.PublicKey.ToByteString()
        };
        await channel.WriteSizeAndProtobufAsync(response);
    }

    private sealed class TestConnectionContext(ILocalPeer peer, Multiaddress remoteAddress) : IConnectionContext
    {
        public ILocalPeer Peer { get; } = peer;
        public State State { get; } = new() { RemoteAddress = remoteAddress };
        public string Id => "plaintext-test";
        public Activity? Activity => null;
        public UpgradeOptions? UpgradeOptions => null;
        public IEnumerable<IProtocol> SubProtocols => [];

        public void ListenerReady(Multiaddress addr)
        {
        }

        public INewConnectionContext CreateConnection() => throw new NotSupportedException();
        public INewConnectionContext CreateConnection<TProtocol>() where TProtocol : IProtocol => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public INewSessionContext UpgradeToSession() => throw new NotSupportedException();
        public IChannel Upgrade(UpgradeOptions? options = null) => throw new NotSupportedException();
        public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel parentChannel, UpgradeOptions? options = null) => Task.CompletedTask;
        public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? options = null) => Task.CompletedTask;
    }
}
