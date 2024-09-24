// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;
internal class TestMuxerTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        ServiceProvider sp = new ServiceCollection()
                  .AddSingleton<IPeerFactoryBuilder>(sp => new TestBuilder(null, sp))
                  .AddSingleton<PeerStore>()
                  .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                  .BuildServiceProvider();

        IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;

        ILocalPeer peerA = peerFactory.Create(TestPeers.Identity(1));
        await peerA.ListenAsync(TestPeers.Multiaddr(1));
        ILocalPeer peerB = peerFactory.Create(TestPeers.Identity(2));
        await peerB.ListenAsync(TestPeers.Multiaddr(2));

        IRemotePeer remotePeerB = await peerA.DialAsync(peerB.Address);
        await remotePeerB.DialAsync<TestPingProtocol>();
    }
}
