// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core.Discovery;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;
internal class TestMuxerTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        ChannelBus channelBus = new();
        ServiceProvider MakeServiceProvider() => new ServiceCollection()
                  .AddSingleton<IPeerFactoryBuilder>(sp => new TestBuilder(sp))
                  .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
                  .AddSingleton<PeerStore>()
                  .AddSingleton(channelBus)
                  .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                  .BuildServiceProvider();

        IPeer peerA = MakeServiceProvider().GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));
        await peerA.StartListenAsync();
        IPeer peerB = MakeServiceProvider().GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(2));
        await peerB.StartListenAsync();

        ISession remotePeerB = await peerA.DialAsync(TestPeers.Multiaddr(2));
        await remotePeerB.DialAsync<TestPingProtocol>();
    }
}
