// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class DuplicateRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [TestCase("success", true)]
    [TestCase("failure", true)]
    [TestCase("closed", true)]
    [TestCase("cancel", true)]
    [TestCase("success", false)]
    [TestCase("failure", false)]
    [TestCase("closed", false)]
    [TestCase("cancel", false)]
    public async Task DuplicateDialWaitsForTheLiveIncomingSession(string outcome, bool preferIncoming)
    {
        Identity[] identities = [TestPeers.Identity(1), TestPeers.Identity(2)];
        Array.Sort(identities, (a, b) => a.PeerId.CompareTo(b.PeerId));
        DuplicateTransport transport = new();
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using GatedPeer peer = new(identities[preferIncoming ? 1 : 0], transport, initialization.Task);
        Multiaddress address = $"/ip4/127.0.0.1/tcp/4001/p2p/{identities[preferIncoming ? 0 : 1].PeerId}";
        using CancellationTokenSource caller = new();
        Task<ISession> dial = peer.DialAsync(address, caller.Token);
        Task<ISession> waiter = peer.DialAsync(address);
        await transport.Started.Task.WaitAsync(Deadline);
        LocalPeer.Session incoming = new(peer) { State = { RemoteAddress = address } };
        using INewSessionContext context = peer.UpgradeToSession(incoming, peer.Transport, true, null);
        transport.Release.TrySetResult();
        await transport.Rejected.Task.WaitAsync(Deadline);
        Assert.That(dial.IsCompleted, Is.False, "Replacement initialization is held at its barrier.");
        try
        {
            if (outcome == "cancel")
            {
                caller.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Deadline));
                Assert.That(waiter.IsCompleted, Is.False);
                Assert.That(incoming.Closed.IsCancellationRequested, Is.False, "An outgoing caller does not own this incoming session.");
                initialization.TrySetResult();
                Assert.That(await waiter.WaitAsync(Deadline), Is.SameAs(incoming));
            }
            else if (outcome == "success")
            {
                initialization.TrySetResult();
                Assert.That(await dial.WaitAsync(Deadline), Is.SameAs(incoming));
                Assert.That(await waiter.WaitAsync(Deadline), Is.SameAs(incoming));
                Assert.That(incoming.Connected.IsCompletedSuccessfully, Is.True);
                Assert.That(incoming.Closed.IsCancellationRequested, Is.False);
            }
            else
            {
                if (outcome == "failure") initialization.TrySetException(new InvalidOperationException("replacement failed"));
                else await incoming.DisconnectAsync();
                Assert.ThrowsAsync<SessionExistsException>(async () => await dial.WaitAsync(Deadline));
                Assert.ThrowsAsync<SessionExistsException>(async () => await waiter.WaitAsync(Deadline));
            }
        }
        finally
        {
            initialization.TrySetResult();
            caller.Cancel();
            await peer.DisposeAsync();
            await ((Task)Task.WhenAll(dial, waiter)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class GatedPeer(Identity identity, DuplicateTransport transport, Task initialization)
        : LocalPeer(identity, null, new ProtocolStackSettings())
    {
        public ProtocolRef Transport { get; } = new(transport);
        protected override ProtocolRef SelectProtocol(Multiaddress addr) => Transport;
        protected override Task ConnectedTo(ISession session, bool isDialer) => initialization;
    }

    private sealed class DuplicateTransport : ITransportProtocol
    {
        public string Id => "/duplicate-transport";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];
        public static bool IsAddressMatch(Multiaddress addr) => true;
        public Task ListenAsync(ITransportContext context, Multiaddress addr, CancellationToken token) => throw new NotSupportedException();
        public async Task DialAsync(ITransportContext context, Multiaddress addr, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            using INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = addr;
            try { connection.UpgradeToSession(); }
            catch (SessionExistsException)
            {
                // Yamux can consume this exception. The core must retain the rejection.
                Rejected.TrySetResult();
            }
        }
    }
}
