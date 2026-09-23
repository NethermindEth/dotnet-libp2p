// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
public class IpTcpPeerIdentityTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Test_DialPassesExpectedPeerIdToSecurityProtocol(bool includePeerId)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Multiaddress dialAddress = includePeerId
            ? $"/ip4/127.0.0.1/tcp/{port}/p2p/{TestPeers.PeerId(2)}"
            : $"/ip4/127.0.0.1/tcp/{port}";

        State state = new();
        TestChannel channel = new();
        TaskCompletionSource<string> upgradedAddress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        INewConnectionContext connection = Substitute.For<INewConnectionContext>();
        connection.State.Returns(state);
        connection.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            // Capture the address before a security protocol can enrich it.
            upgradedAddress.SetResult(state.RemoteAddress!.ToString());
            return channel;
        });
        ITransportContext context = Substitute.For<ITransportContext>();
        context.CreateConnection().Returns(connection);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task dialTask = new IpTcpProtocol().DialAsync(context, dialAddress, cancellation.Token);
        try
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(cancellation.Token);
            string actualAddress = await upgradedAddress.Task.WaitAsync(cancellation.Token);
            Assert.That(actualAddress, Is.EqualTo(dialAddress.ToString()));
        }
        finally
        {
            await channel.CloseAsync();
            await dialTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
}
