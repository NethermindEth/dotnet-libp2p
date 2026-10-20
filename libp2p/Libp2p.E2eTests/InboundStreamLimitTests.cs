// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using NUnit.Framework;

namespace Libp2p.E2eTests;

[NonParallelizable]
public class InboundStreamLimitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static Gate _gate = new();

    [SetUp]
    public void ResetGate() => _gate = new();

    [Test]
    public async Task AnInboundStreamBeyondTheLimitIsRefused()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);

        try
        {
            Task first = session.DialAsync<GatedProtocol>();
            await NextListenerAsync();
            await AssertRefusedBeforeDispatchAsync(session.DialAsync<GatedProtocol>());
            _gate.Release.TrySetResult();
            await first.WaitAsync(Timeout);
        }
        finally
        {
            _gate.Release.TrySetResult();
        }
    }

    [Test]
    public async Task TheAllowanceComesBackWhenTheStreamEnds()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);

        _gate.Release.TrySetResult();
        await session.DialAsync<GatedProtocol>().WaitAsync(Timeout);
        await session.DialAsync<GatedProtocol>().WaitAsync(Timeout);
    }

    [Test]
    public async Task VersionsSharingOneLimitShareOneAllowance()
    {
        // A protocol offered under several versions has alternatives, not separate allowances.
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);

        try
        {
            Task first = session.DialAsync<GatedProtocol>();
            await NextListenerAsync();
            await AssertRefusedAsync(session.DialAsync<OtherVersionProtocol>());
            _gate.Release.TrySetResult();
            await first.WaitAsync(Timeout);
        }
        finally
        {
            _gate.Release.TrySetResult();
        }
    }

    [Test]
    public async Task AnUnlimitedProtocolIsUntouched()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);

        try
        {
            Task first = session.DialAsync<GatedProtocol>();
            await NextListenerAsync();
            await session.DialAsync<UnlimitedProtocol>().WaitAsync(Timeout);
            _gate.Release.TrySetResult();
            await first.WaitAsync(Timeout);
        }
        finally
        {
            _gate.Release.TrySetResult();
        }
    }

    [Test]
    public async Task TheCapStillBindsAfterEarlierStreamsFinish()
    {
        // Both peers share the limit: an outbound refund would widen the inbound cap on each dial.
        InboundStreamLimit limit = new(maxConcurrent: 1);
        await using LimitedE2eTestSetup test = new(limit);
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);

        try
        {
            _gate.Release.TrySetResult();
            for (int i = 0; i < 3; i++)
            {
                await session.DialAsync<GatedProtocol>().WaitAsync(Timeout);
            }

            _gate = new();
            Task first = session.DialAsync<GatedProtocol>();
            await NextListenerAsync();
            await AssertRefusedBeforeDispatchAsync(session.DialAsync<GatedProtocol>());
            _gate.Release.TrySetResult();
            await first.WaitAsync(Timeout);
        }
        finally
        {
            _gate.Release.TrySetResult();
        }
    }

    [Test]
    public async Task PerPeerLimitUsesTheAuthenticatedRemoteIdentity()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 2, maxPerPeer: 1));
        await test.AddPeersAsync(3);
        ISession firstPeer = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);
        ISession otherPeer = await test.Peers[2].DialAsync([.. test.Peers[1].ListenAddresses]).WaitAsync(Timeout);

        try
        {
            Task first = firstPeer.DialAsync<GatedProtocol>();
            Assert.That(await NextListenerAsync(), Is.EqualTo(TestPeers.PeerId(0)));
            await AssertRefusedBeforeDispatchAsync(firstPeer.DialAsync<GatedProtocol>());
            Task other = otherPeer.DialAsync<GatedProtocol>();
            Assert.That(await NextListenerAsync(), Is.EqualTo(TestPeers.PeerId(2)), "Another identity still has room under the total.");
            _gate.Release.TrySetResult();
            await Task.WhenAll(first, other).WaitAsync(Timeout);
        }
        finally
        {
            _gate.Release.TrySetResult();
        }
    }

    private static Task<PeerId> NextListenerAsync() => _gate.Entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

    private static async Task AssertRefusedBeforeDispatchAsync(Task dial)
    {
        using CancellationTokenSource cancellation = new();
        Task<PeerId> dispatched = _gate.Entered.Reader.ReadAsync(cancellation.Token).AsTask();
        try
        {
            Assert.That(await Task.WhenAny(dial, dispatched).WaitAsync(Timeout), Is.SameAs(dial),
                "A stream beyond the allowance must be refused before the listener runs.");
            await AssertRefusedAsync(dial);
        }
        finally
        {
            cancellation.Cancel();
            await ((Task)dispatched).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertRefusedAsync(Task dial)
    {
        Exception? failure = null;
        try
        {
            await dial.WaitAsync(Timeout);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        Assert.That(failure, Is.TypeOf<ChannelClosedException>());
    }

    private static async Task ReadReplyAsync(IChannel channel)
    {
        await channel.WriteLineAsync("ping");
        Assert.That(await channel.ReadLineAsync(), Is.EqualTo("pong"));
        // EOF follows server-side allowance release, so a subsequent dial cannot race that release.
        Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
    }

    private sealed class Gate
    {
        public System.Threading.Channels.Channel<PeerId> Entered { get; } = System.Threading.Channels.Channel.CreateUnbounded<PeerId>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LimitedE2eTestSetup(InboundStreamLimit limit) : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];

        protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
        {
            builder.AddProtocol<GatedProtocol>(inboundLimit: limit);
            builder.AddProtocol<OtherVersionProtocol>(inboundLimit: limit);
            builder.AddProtocol<UnlimitedProtocol>();
            return builder;
        }
    }

    private sealed class GatedProtocol : ISessionProtocol
    {
        public string Id => "/test/gated/1.0.0";
        public Task DialAsync(IChannel downChannel, ISessionContext context) => ReadReplyAsync(downChannel);

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            Gate gate = _gate;
            await downChannel.ReadLineAsync();
            gate.Entered.Writer.TryWrite(context.State.RemotePeerId!);
            await gate.Release.Task.WaitAsync(Timeout);
            await downChannel.WriteLineAsync("pong");
        }
    }

    private sealed class OtherVersionProtocol : ISessionProtocol
    {
        public string Id => "/test/gated/2.0.0";
        public Task DialAsync(IChannel downChannel, ISessionContext context) => ReadReplyAsync(downChannel);

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.ReadLineAsync();
            await downChannel.WriteLineAsync("pong");
        }
    }

    private sealed class UnlimitedProtocol : ISessionProtocol
    {
        public string Id => "/test/unlimited/1.0.0";
        public Task DialAsync(IChannel downChannel, ISessionContext context) => ReadReplyAsync(downChannel);

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.ReadLineAsync();
            await downChannel.WriteLineAsync("pong");
        }
    }
}
