// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class SessionLifetimeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task BothSessionLifetimesEndWhenEitherPeerDisconnects(bool listenerDisconnects)
    {
        await using LifetimeSetup setup = new();
        await setup.AddPeersAsync(2);
        TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Peers[1].OnConnected += session => accepted.TrySetResult(session);
        ISession outgoing = await setup.Peers[0].DialAsync([.. setup.Peers[1].ListenAddresses]);
        ISession incoming = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(outgoing.RemotePeerId, Is.EqualTo(setup.Peers[1].Identity.PeerId));
        Assert.That(incoming.RemotePeerId, Is.EqualTo(setup.Peers[0].Identity.PeerId));
        Assert.That(outgoing.Closed.CanBeCanceled, Is.True);
        Assert.That(incoming.Closed.CanBeCanceled, Is.True);
        CancellationToken outgoingToken = outgoing.Closed;
        CancellationToken incomingToken = incoming.Closed;
        TaskCompletionSource outgoingClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incomingClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration outgoingRegistration = outgoingToken.Register(() => outgoingClosed.TrySetResult());
        using CancellationTokenRegistration incomingRegistration = incomingToken.Register(() => incomingClosed.TrySetResult());

        await (listenerDisconnects ? incoming : outgoing).DisconnectAsync();

        await Task.WhenAll(outgoingClosed.Task, incomingClosed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(outgoing.Closed, Is.EqualTo(outgoingToken));
        Assert.That(incoming.Closed, Is.EqualTo(incomingToken));
        Assert.That(outgoing.Closed.IsCancellationRequested, Is.True);
        Assert.That(incoming.Closed.IsCancellationRequested, Is.True);
    }

    private sealed class LifetimeSetup : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];
    }
}
