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
        ServiceProvider sp = new ServiceCollection()
                  .AddSingleton<IPeerFactoryBuilder>(sp => new TestBuilder(sp))
                  .AddSingleton<PeerStore>()
                  .AddSingleton<ChannelBus>()
                  .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                  .BuildServiceProvider();

        IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;

        IPeer peerA = peerFactory.Create(TestPeers.Identity(1));
        await peerA.StartListenAsync([TestPeers.Multiaddr(1)]);
        IPeer peerB = peerFactory.Create(TestPeers.Identity(2));
        await peerB.StartListenAsync([TestPeers.Multiaddr(2)]);

        ISession remotePeerB = await peerA.DialAsync(TestPeers.Multiaddr(1));
        await remotePeerB.DialAsync<TestPingProtocol>();
    }
}
