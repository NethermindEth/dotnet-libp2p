// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class InboundStreamLimitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task AnInboundStreamBeyondTheLimitIsRefused()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        try
        {
            Task first = session.DialAsync<GatedProtocol>();
            Assert.That(GatedProtocol.Listening.Wait(Timeout), Is.True, "The first stream never reached the protocol.");

            Assert.ThrowsAsync<ChannelClosedException>(async () => await session.DialAsync<GatedProtocol>(),
                "The second stream is over the allowance and must be refused.");

            GatedProtocol.Release.Set();
            Assert.That(first.Wait(Timeout), Is.True);
        }
        finally
        {
            GatedProtocol.Release.Set();
        }
    }

    [Test]
    public async Task TheAllowanceComesBackWhenTheStreamEnds()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        GatedProtocol.Release.Set();
        await session.DialAsync<GatedProtocol>().WaitAsync(Timeout);

        Assert.DoesNotThrowAsync(async () => await session.DialAsync<GatedProtocol>().WaitAsync(Timeout),
            "A stream that finished must give its allowance back.");
    }

    [Test]
    public async Task VersionsSharingOneLimitShareOneAllowance()
    {
        // The case this exists for: a protocol offered under several versions is a set of
        // alternatives, so the versions must not each bring their own allowance.
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        try
        {
            Task first = session.DialAsync<GatedProtocol>();
            Assert.That(GatedProtocol.Listening.Wait(Timeout), Is.True);

            Assert.ThrowsAsync<ChannelClosedException>(async () => await session.DialAsync<OtherVersionProtocol>(),
                "The other version draws on the same allowance.");

            GatedProtocol.Release.Set();
            Assert.That(first.Wait(Timeout), Is.True);
        }
        finally
        {
            GatedProtocol.Release.Set();
        }
    }

    [Test]
    public async Task AnUnlimitedProtocolIsUntouched()
    {
        await using LimitedE2eTestSetup test = new(new InboundStreamLimit(maxConcurrent: 1));
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        try
        {
            Task first = session.DialAsync<GatedProtocol>();
            Assert.That(GatedProtocol.Listening.Wait(Timeout), Is.True, "The limited protocol holds the only allowance.");

            Assert.DoesNotThrowAsync(async () => await session.DialAsync<UnlimitedProtocol>().WaitAsync(Timeout),
                "A protocol registered without a limit must not be gated by another protocol's.");

            GatedProtocol.Release.Set();
            Assert.That(first.Wait(Timeout), Is.True);
        }
        finally
        {
            GatedProtocol.Release.Set();
        }
    }

    [Test]
    public async Task TheCapStillBindsAfterEarlierStreamsFinish()
    {
        // Both peers register the protocol against the same limit, so this covers the dial path too:
        // releasing an allowance that was never acquired drives the count below zero and widens the
        // cap by one for every stream dialled, until the limit stops limiting anything.
        InboundStreamLimit limit = new(maxConcurrent: 1);
        await using LimitedE2eTestSetup test = new(limit);
        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        try
        {
            GatedProtocol.Release.Set();
            for (int i = 0; i < 3; i++)
            {
                await session.DialAsync<GatedProtocol>().WaitAsync(Timeout);
            }

            GatedProtocol.Listening.Reset();
            GatedProtocol.Release.Reset();

            Task first = session.DialAsync<GatedProtocol>();
            Assert.That(GatedProtocol.Listening.Wait(Timeout), Is.True, "The first stream never reached the protocol.");

            Assert.ThrowsAsync<ChannelClosedException>(async () => await session.DialAsync<GatedProtocol>(),
                "The allowance is still one, however many streams have come and gone.");

            GatedProtocol.Release.Set();
            Assert.That(first.Wait(Timeout), Is.True);
        }
        finally
        {
            GatedProtocol.Release.Set();
        }
    }

    [TearDown]
    public void ResetGate()
    {
        GatedProtocol.Listening.Reset();
        GatedProtocol.Release.Reset();
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
        internal static readonly ManualResetEventSlim Listening = new(false);
        internal static readonly ManualResetEventSlim Release = new(false);

        public string Id => "/test/gated/1.0.0";

        public async Task DialAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.WriteLineAsync("ping");
            await downChannel.ReadLineAsync();
        }

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.ReadLineAsync();
            Listening.Set();
            Release.Wait(Timeout);
            await downChannel.WriteLineAsync("pong");
        }
    }

    private sealed class OtherVersionProtocol : ISessionProtocol
    {
        public string Id => "/test/gated/2.0.0";

        public async Task DialAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.WriteLineAsync("ping");
            await downChannel.ReadLineAsync();
        }

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.ReadLineAsync();
            await downChannel.WriteLineAsync("pong");
        }
    }

    private sealed class UnlimitedProtocol : ISessionProtocol
    {
        public string Id => "/test/unlimited/1.0.0";

        public async Task DialAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.WriteLineAsync("ping");
            await downChannel.ReadLineAsync();
        }

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            await downChannel.ReadLineAsync();
            await downChannel.WriteLineAsync("pong");
        }
    }
}
