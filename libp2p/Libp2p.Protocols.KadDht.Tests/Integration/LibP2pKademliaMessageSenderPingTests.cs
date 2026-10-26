// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

[TestFixture]
public class LibP2pKademliaMessageSenderPingTests
{
    private const string Address = "/ip4/127.0.0.1/tcp/4001";

    private ILocalPeer _localPeer = null!;
    private ISession _session = null!;
    private DhtNode _node = null!;

    [SetUp]
    public void Setup()
    {
        _localPeer = Substitute.For<ILocalPeer>();
        _localPeer.GetProtocol<PingProtocol>().Returns(new PingProtocol());
        _session = Substitute.For<ISession>();
        _localPeer.DialAsync(Multiaddress.Decode(Address), Arg.Any<CancellationToken>()).Returns(_session);

        var keyBytes = Enumerable.Repeat((byte)7, 32).ToArray();
        _node = new DhtNode
        {
            PublicKey = new PublicKey(keyBytes),
            PeerId = new PeerId(keyBytes),
            Multiaddrs = new[] { Address }
        };
    }

    [TearDown]
    public async Task TearDown() => await _localPeer.DisposeAsync();

    [Test]
    public async Task Ping_OperationDeadlineExpires_DisconnectsSessionAndReportsFailure()
    {
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromMilliseconds(100));

        bool alive = await sender.Ping(_node, CancellationToken.None);

        Assert.That(alive, Is.False);
        await _session.Received(1).DisconnectAsync();
    }

    [Test]
    public async Task Ping_CallerCancels_KeepsSessionAndPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(call =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
        });
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromSeconds(30));

        Assert.That(async () => await sender.Ping(_node, cts.Token), Throws.InstanceOf<OperationCanceledException>());
        await _session.DidNotReceive().DisconnectAsync();
    }
}
