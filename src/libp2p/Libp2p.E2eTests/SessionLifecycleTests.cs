// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class SessionLifecycleTests
{
    [Test]
    public async Task Sessions_ReflectConnectionLifecycleThroughInterface()
    {
        await using SessionLifecycleE2eTestSetup test = new();
        await test.AddPeersAsync(2);

        ILocalPeer dialer = test.Peers[0];
        ILocalPeer listener = test.Peers[1];
        IReadOnlyCollection<ISession> outgoingSessions = dialer.Sessions;
        IReadOnlyCollection<ISession> incomingSessions = listener.Sessions;

        Assert.Multiple(() =>
        {
            Assert.That(outgoingSessions, Is.Empty);
            Assert.That(incomingSessions, Is.Empty);
        });

        TaskCompletionSource<ISession> incomingSessionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnConnected += session => incomingSessionSource.TrySetResult(session);

        ISession outgoingSession = await dialer.DialAsync([.. listener.ListenAddresses]);
        ISession incomingSession = await incomingSessionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(outgoingSessions.Single(), Is.SameAs(outgoingSession));
            Assert.That(incomingSessions.Single(), Is.SameAs(incomingSession));
        });

        await outgoingSession.DisconnectAsync();

        Assert.That(outgoingSessions, Is.Empty);
    }

    private sealed class SessionLifecycleE2eTestSetup : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index)
            => [$"/ip4/127.0.0.1/tcp/0"];
    }
}
