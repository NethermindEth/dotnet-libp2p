// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core.Tests;
public class ContextTests
{
    public static Channel tcp = new Channel();


    [Test]
    public async Task E2e()
    {
        ProtocolRef tProto = new ProtocolRef(new TProto());
        ProtocolRef cProto = new ProtocolRef(new CProto());
        ProtocolRef sProto = new ProtocolRef(new SProto());

        ProtocolStackSettings protocolStackSettings = new()
        {
            Protocols = new Dictionary<ProtocolRef, ProtocolRef[]>
            {
                { tProto, [ cProto] },
                { cProto, [sProto] },
                { sProto, [] },
            },
            TopProtocols = [tProto]
        };

        LocalPeer peer1 = new LocalPeer(new Identity(), protocolStackSettings);
        LocalPeer peer2 = new LocalPeer(new Identity(), protocolStackSettings);

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
        //ISession connectionSessionContext = cContext.UpgradeToSession();

        //ISessionContext sContext = peer.CreateContext(sProto);

        //sContext.SubDial();
        //sContext.DialAsync<SProto2>();

        //sContext.Disconnect();
        await Task.Delay(1000_0000);
    }
}

class ProtocolStackSettings : IProtocolStackSettings
{
    public Dictionary<ProtocolRef, ProtocolRef[]>? Protocols { get; set; } = [];
    public ProtocolRef[]? TopProtocols { get; set; } = [];
}

class TProto : ITransportProtocol
{
    public string Id => nameof(TProto);

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        try
        {
            context.ListenerReady(Multiaddress.Decode("/ip4/127.0.0.1/tcp/4096"));
            using INewConnectionContext connectionCtx = context.CreateConnection();
            connectionCtx.State.RemoteAddress = Multiaddress.Decode("/ip4/127.0.0.1/tcp/1000");

            IChannel topChan = connectionCtx.Upgrade();
            connectionCtx.Token.Register(() => topChan.CloseAsync());


            ReadResult received;
            while (true)
            {
                received = await ContextTests.tcp.ReadAsync(1, ReadBlockingMode.WaitAny);
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

    public async Task DialAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        INewConnectionContext connectionContext = context.CreateConnection();
        IChannel topChan = connectionContext.Upgrade();
        connectionContext.Token.Register(() => topChan.CloseAsync());


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
            using INewSessionContext session = context.UpgradeToSession();
            IChannel topChan = context.Upgrade();

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
            using INewSessionContext session = context.UpgradeToSession();
            IChannel topChan = context.Upgrade();

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
