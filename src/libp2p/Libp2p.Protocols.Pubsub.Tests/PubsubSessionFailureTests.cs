// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using System.Buffers;
using System.Diagnostics;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(5_000)]
public class PubsubSessionFailureTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ListenAsync_Eof_DoesNotMarkActivityAsErrorAndReconnects(bool outboundFirst)
    {
        await CheckSessionFailure([], outboundFirst, protocolViolation: false);
    }

    [TestCase(false, new byte[] { 1, 0 })] // Invalid protobuf tag in a small frame.
    [TestCase(true, new byte[] { 1, 0 })]
    [TestCase(false, new byte[] { 0x81, 0x08 })] // 1,025 bytes, exceeding the configured limit.
    [TestCase(true, new byte[] { 0x81, 0x08 })]
    [TestCase(false, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 2 })]
    [TestCase(true, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 2 })]
    public async Task ListenAsync_InvalidFrame_DisconnectsWithoutReconnecting(bool outboundFirst, byte[] frame)
    {
        await CheckSessionFailure(frame, outboundFirst, protocolViolation: true);
    }

    private static async Task CheckSessionFailure(byte[] frame, bool outboundFirst, bool protocolViolation)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { MaxRpcBytes = 1_024, ReconnectionPeriod = 10 });
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(1));
        localPeer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        TaskCompletionSource redial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            redial.TrySetResult();
            return new TestRemotePeer(remoteAddress);
        });

        await router.StartAsync(localPeer, cts.Token);
        using Activity activity = new Activity("pubsub-listen").Start();
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = remoteAddress });
        context.Activity.Returns(activity);
        TestChannel channel = new();
        TaskCompletionSource outboundClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OpenOutbound() => router.OutboundConnection(remoteAddress, PubsubRouter.FloodsubProtocolVersion, outboundClosed.Task, _ => { });
        if (outboundFirst)
        {
            OpenOutbound();
        }
        else
        {
            context.DialAsync(Arg.Any<ISessionProtocol>()).Returns(_ =>
            {
                OpenOutbound();
                return Task.CompletedTask;
            });
        }
        context.DisconnectAsync().Returns(async _ =>
        {
            outboundClosed.TrySetResult();
            await channel.CloseAsync();
        });

        try
        {
            Task listen = new FloodsubProtocol(router).ListenAsync(channel, context);
            if (frame.Length == 0)
            {
                await channel.Reverse().WriteEofAsync(cts.Token);
            }
            else
            {
                await channel.Reverse().WriteAsync(new ReadOnlySequence<byte>(frame), cts.Token);
            }
            await listen.WaitAsync(cts.Token);
            outboundClosed.TrySetResult();

            if (protocolViolation)
            {
                await context.Received(1).DisconnectAsync();
                Assert.That(activity.Status, Is.EqualTo(ActivityStatusCode.Error));
                await Task.Delay(200, cts.Token);
                Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Is.Empty);
                Assert.That(redial.Task.IsCompleted, Is.False, "A protocol violator must not be redialed by either stream's cleanup.");
            }
            else
            {
                await context.DidNotReceive().DisconnectAsync();
                Assert.That(activity.Status, Is.EqualTo(ActivityStatusCode.Unset));
                await redial.Task.WaitAsync(cts.Token);
            }
        }
        finally
        {
            await cts.CancelAsync();
            await channel.CloseAsync();
            outboundClosed.TrySetResult();
        }
    }
}
