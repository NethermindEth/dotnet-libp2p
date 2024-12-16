// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class NoiseProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        // Arrange
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");

        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto2.Id.Returns("proto2");

        // Dialer
        MultiplexerSettings dialerSettings = new();
        dialerSettings.Add(proto2);
        dialerSettings.Add(proto1);

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });


        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        NoiseProtocol dialer = new(dialerSettings);

        // Listener
        MultiplexerSettings listenerSettings = new();
        listenerSettings.Add(proto1);

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        NoiseProtocol listener = new(listenerSettings);

        // Act
        Task listenTask = listener.ListenAsync(downChannel, listenerContext);
        Task dialTask = dialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        int sent = 42;
        ValueTask<IOResult> writeTask = dialerUpChannel.Reverse().WriteVarintAsync(sent);
        int received = await listenerUpChannel.Reverse().ReadVarintAsync();
        await writeTask;

        await dialerUpChannel.CloseAsync();
        await listenerUpChannel.CloseAsync();
        await downChannel.CloseAsync();

        await dialTask;
        await listenTask;

        Assert.That(received, Is.EqualTo(sent));
    }
}
