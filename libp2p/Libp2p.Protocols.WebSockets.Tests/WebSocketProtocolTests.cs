// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

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
        Assert.That(listener.ListenAddresses.Single().Get<TCP>().ToString(), Is.Not.EqualTo("0"));

        ISession session = await dialer.DialAsync(listener.ListenAddresses.ToArray());
        int response = await session.DialAsync<IncrementNumberProtocol, int, int>(41);

        Assert.That(response, Is.EqualTo(42));
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
}
