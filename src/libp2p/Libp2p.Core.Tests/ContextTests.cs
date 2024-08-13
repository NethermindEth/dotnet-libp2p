// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using NUnit.Framework.Constraints;
using System.Net;

namespace Nethermind.Libp2p.Core.Tests;
public class ContextTests
{
    public static Channel tcp = new Channel();


    [Test]
    public async Task E2e()
    {
        ITransportProtocol tProto = new TProto();
        IConnectionProtocol cProto = new CProto();
        ISessionProtocol sProto = new SProto();

        BuilderContext builderContext = new BuilderContext
        {
            Protocols = new Dictionary<IProtocol, IProtocol[]>
        {
            { tProto, [ cProto] },
            { cProto, [sProto] },
            { sProto, [] },
        },
            TopProtocols = [tProto]
        };

        LocalPeer peer1 = new LocalPeer(builderContext, new Identity());
        LocalPeer peer2 = new LocalPeer(builderContext, new Identity());

        await peer1.StartListenAsync([new Multiaddress()]);
        await peer2.StartListenAsync([new Multiaddress()]);

        ISession session = await peer2.DialAsync(new Multiaddress());

        //await session.DialAsync<SProto>();
        //await session.DialAsync<SProto2>();

        //ITransportContext tContext = peer.CreateContext(tProto);

        //ITransportConnectionContext tcContext = tContext.CreateConnection();
        //tcContext.SubDial();

        //IConnectionContext cContext = peer.CreateContext(cProto);

        //cContext.SubDial();
        //ISession connectionSessionContext = cContext.CreateSession();

        //ISessionContext sContext = peer.CreateContext(sProto);

        //sContext.SubDial();
        //sContext.DialAsync<SProto2>();

        //sContext.Disconnect();
        await Task.Delay(1000_0000);
    }
}

class BuilderContext : IBuilderContext
{
    public Dictionary<IProtocol, IProtocol[]>? Protocols { get; set; } = new Dictionary<IProtocol, IProtocol[]> { };
    public IProtocol[]? TopProtocols { get; set; } = [];
}

class TProto : ITransportProtocol
{
    public string Id => nameof(TProto);

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        try
        {
            context.ListenerReady(Multiaddress.Decode("/ip4/127.0.0.1/tcp/4096"));
            using ITransportConnectionContext connectionCtx = context.CreateConnection();


            IChannel topChan = connectionCtx.SubListen();
            connectionCtx.Token.Register(() => topChan.CloseAsync());


            ReadResult received;
            while (true)
            {
                received = await ContextTests.tcp.ReadAsync(1, ReadBlockingMode.WaitAny);
                if(received.Result != IOResult.Ok)
                {
                    break;
                }

                var sent = await topChan.WriteAsync(received.Data);

                if (sent!= IOResult.Ok)
                {
                    break;
                }
            }
            await topChan.CloseAsync();
        }
        catch
        {

        }
    }

    public async Task DialAsync(ITransportConnectionContext context, Multiaddress listenAddr, CancellationToken token)
    {
        IChannel topChan = context.SubDial();
        context.Token.Register(() => topChan.CloseAsync());


        ReadResult received;
        while (true)
        {
            received = await topChan.ReadAsync(1, ReadBlockingMode.WaitAny);
            if (received.Result != IOResult.Ok)
            {
                break;
            }

            var sent = await ContextTests.tcp.WriteAsync(received.Data);

            if (sent != IOResult.Ok)
            {
                break;
            }
        }
        await topChan.CloseAsync();
    }
}

class CProto : IConnectionProtocol
{
    public string Id => throw new NotImplementedException();

    public async Task DialAsync(IChannel downChannel, IConnectionContext context)
    {
        try
        {
            using ISession session = context.CreateSession(new PeerId(new Dto.PublicKey()));
            IChannel topChan = context.SubDial();

            ReadResult received;
            while (true)
            {
                received = await topChan.ReadAsync(1, ReadBlockingMode.WaitAny);
                if (received.Result != IOResult.Ok)
                {
                    break;
                }

                var sent = await downChannel.WriteAsync(received.Data);

                if (sent != IOResult.Ok)
                {
                    break;
                }
            }
            await topChan.CloseAsync();
        }
        catch
        {

        }
    }

    public async Task ListenAsync(IChannel downChannel, IConnectionContext context)
    {
        try
        {
            using ISession session = context.CreateSession();
            IChannel topChan = context.SubListen();

            ReadResult received;
            while (true)
            {
                received = await downChannel.ReadAsync(1, ReadBlockingMode.WaitAny);
                if (received.Result != IOResult.Ok)
                {
                    break;
                }

                var sent = await topChan.WriteAsync(received.Data);

                if (sent != IOResult.Ok)
                {
                    break;
                }
            }
            await topChan.CloseAsync();
        }
        catch
        {

        }
    }
}

class SProto : ISessionProtocol
{
    public string Id => throw new NotImplementedException();

    public async Task DialAsync(IChannel downChannel, ISessionContext context)
    {
        try
        {
            await downChannel.WriteLineAsync("Oh hi there");
        }
        catch
        {

        }
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        try
        {
            var line = await downChannel.ReadLineAsync();
        }
        catch
        {

        }
    }
}

class SProto2 : ISessionProtocol
{
    public string Id => throw new NotImplementedException();

    public Task DialAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }
}
