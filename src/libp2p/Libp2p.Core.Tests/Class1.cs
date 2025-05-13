// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Core.TestsBase;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

internal class StackTests
{
    public async Task<ISession> Init(int? delay = null, bool cancelOnToken = true)
    {

        IPeerFactory factory = new StackBuilder(new ServiceCollection()
                .AddSingleton<IProtocolStackSettings>(new ProtocolStackSettings())
                .AddSingleton(new Transport(new Channel()))
                .AddSingleton(new IncrementNumberTestProtocol(delay, cancelOnToken))
                .BuildServiceProvider())
            .AddAppLayerProtocol<IncrementNumberTestProtocol>().Build();

        var peer1 = factory.Create(TestPeers.Identity(1));
        var peer2 = factory.Create(TestPeers.Identity(2));

        await peer1.StartListenAsync();

        return await peer2.DialAsync(peer1.ListenAddresses.First());
    }

    [Test]
    public async Task Test_SimpleStack()
    {
        ISession session = await Init();
        int res = await session.DialAsync<IncrementNumberTestProtocol, int, int>(42);
        Assert.That(res, Is.EqualTo(43));
    }

    [Test]
    public async Task Test_Cancellation_WhenNotYetDialed()
    {
        ISession session = await Init();
        CancellationToken t = new(true);

        try
        {
            await session.DialAsync<IncrementNumberTestProtocol, int, int>(42, t);
        }
        catch (OperationCanceledException)
        {
            Assert.Pass();
            return;
        }
        Assert.Fail();
    }


    [Test]
    public async Task Test_Cancellation_WhenDialed()
    {
        ISession session = await Init(2000, false);

        CancellationToken t = new CancellationTokenSource(1000).Token;

        try
        {
            await session.DialAsync<IncrementNumberTestProtocol, int, int>(42, t);
        }
        catch (OperationCanceledException)
        {
            Assert.Pass();
            return;
        }
        Assert.Fail();
    }

    [Ignore("Not yet implemented.")]
    [Test]
    public async Task Test_Cancellation_WhenDialedButWithTokenIgnored()
    {
        ISession session = await Init(2000, false);

        CancellationToken t = new CancellationTokenSource(1000).Token;

        try
        {
            await session.DialAsync<IncrementNumberTestProtocol, int, int>(42, t);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail();
        }
        Assert.Pass();
    }
}

class StackBuilder(IServiceProvider serviceProvider) : PeerFactoryBuilderBase<StackBuilder, PeerFactory>(serviceProvider)
{
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef transport = Get<Transport>();
        Connect([transport], [.. additionalProtocols]);
        return [transport];
    }
}

class Transport(Channel p2p) : ITransportProtocol
{
    public string Id => "t";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId)
    {
        return [$"/p2p/{peerId}"];
    }

    public static bool IsAddressMatch(Multiaddress addr)
    {
        return true;
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        IChannel downChan = p2p.Reverse;
        await downChan.WriteLineAsync($"/p2p/{context.Peer.Identity.PeerId}");
        var conCtx = context.CreateConnection();
        conCtx.State.RemoteAddress = remoteAddr;
        var ses = conCtx.UpgradeToSession();

        foreach (var req in ses.DialRequests)
        {
            IChannel upChan = conCtx.Upgrade(req);

            Task t1 = Task.Run(async () =>
            {
                await foreach (var line in downChan.ReadAllAsync())
                {
                    await upChan.WriteAsync(line);
                }
            });

            Task t2 = Task.Run(async () =>
            {
                await foreach (var line in upChan.ReadAllAsync())
                {
                    await downChan.WriteAsync(line);
                }
            });

            await Task.WhenAll(t1, t2);
        }
    }

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        IChannel downChan = p2p;
        context.ListenerReady(listenAddr);
        var addr = await downChan.ReadLineAsync();
        var conCtx = context.CreateConnection();
        conCtx.State.RemoteAddress = addr;
        var ses = conCtx.UpgradeToSession();

        IChannel upChan = conCtx.Upgrade();

        while (true)
        {
            token.ThrowIfCancellationRequested();

            Task t1 = Task.Run(async () =>
            {
                await foreach (var line in downChan.ReadAllAsync())
                {
                    await upChan.WriteAsync(line);
                }
            });

            Task t2 = Task.Run(async () =>
            {
                await foreach (var line in upChan.ReadAllAsync())
                {
                    await downChan.WriteAsync(line);
                }
            });

            await Task.WhenAll(t1, t2);
        }
    }
}
