// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;
internal class TestMuxerTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {

        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {

        };
        IPeerFactory peerFactory = new TestBuilder().Build();

        ILocalPeer peerA = peerFactory.Create(TestPeers.Identity(1));
        await peerA.ListenAsync(TestPeers.Multiaddr(1));
        ILocalPeer peerB = peerFactory.Create(TestPeers.Identity(2));
        await peerB.ListenAsync(TestPeers.Multiaddr(2));

        IRemotePeer remotePeerB = await peerA.DialAsync(peerB.Address);
        await remotePeerB.DialAsync<TestPingProtocol>();
    }
}
