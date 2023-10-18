// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;
using MultiaddrEnum = Nethermind.Libp2p.Core.Enums.Multiaddr;

namespace Nethermind.Libp2p.Protocols;

// TODO: Rewrite with SocketAsyncEventArgs
public class IpTcpProtocol : IProtocol
{
    private readonly ILogger? _logger;

    public IpTcpProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();
    }

    public string Id => "ip-tcp";

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("ListenAsync({contextId})", context.Id);

        Multiaddr addr = context.LocalPeer.Address;
        MultiaddrEnum ipProtocol = addr.Has(MultiaddrEnum.Ip4) ? MultiaddrEnum.Ip4 : MultiaddrEnum.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int tcpPort = int.Parse(addr.At(MultiaddrEnum.Tcp)!);

        Socket srv = new(SocketType.Stream, ProtocolType.Tcp);
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen(tcpPort);

        IPEndPoint localIpEndpoint = (IPEndPoint)srv.LocalEndPoint!;
        channel.OnClose(() =>
        {
            srv.Close();
            return Task.CompletedTask;
        });

        context.LocalEndpoint = Multiaddr.From(
              ipProtocol, ipProtocol == MultiaddrEnum.Ip4 ?
                localIpEndpoint.Address.MapToIPv4().ToString() :
                localIpEndpoint.Address.MapToIPv6().ToString(),
              MultiaddrEnum.Tcp, localIpEndpoint.Port);

        if (tcpPort == 0)
        {
            context.LocalPeer.Address = context.LocalPeer.Address
                .Replace(MultiaddrEnum.Tcp, localIpEndpoint.Port.ToString());
        }

        _logger?.LogDebug("Ready to handle connections");
        context.ListenerReady();

        await Task.Run(async () =>
        {
            while (!channel.IsClosed)
            {
                Socket client = await srv.AcceptAsync();
                IPeerContext clientContext = context.Fork();
                IPEndPoint remoteIpEndpoint = (IPEndPoint)client.RemoteEndPoint!;

                clientContext.RemoteEndpoint = clientContext.RemotePeer.Address = Multiaddr.From(
                    ipProtocol, remoteIpEndpoint.Address.ToString(),
                    MultiaddrEnum.Tcp, remoteIpEndpoint.Port);

                IChannel chan = channelFactory.SubListen(clientContext);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!chan.IsClosed)
                        {
                            if (client.Available == 0)
                            {
                                await Task.Yield();
                            }

                            byte[] buf = new byte[client.Available];
                            int len = await client.ReceiveAsync(buf, SocketFlags.None);
                            if (len != 0)
                            {
                                await chan.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        await chan.CloseAsync(false);
                    }
                }, chan.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ReadOnlySequence<byte> data in chan.ReadAllAsync())
                        {
                            await client.SendAsync(data.ToArray(), SocketFlags.None);
                        }
                    }
                    catch (SocketException)
                    {
                        _logger?.LogInformation($"Disconnected({context.Id}) due to a socket exception");
                        await chan.CloseAsync(false);
                    }
                }, chan.Token);
            }
        });
    }

    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("DialAsync({contextId})", context.Id);

        TaskCompletionSource<bool?> waitForStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        Multiaddr addr = context.RemotePeer.Address;
        MultiaddrEnum ipProtocol = addr.Has(MultiaddrEnum.Ip4) ? MultiaddrEnum.Ip4 : MultiaddrEnum.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int tcpPort = int.Parse(addr.At(MultiaddrEnum.Tcp)!);
        try
        {
            await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort), channel.Token);
        }
        catch (SocketException)
        {
            _logger?.LogInformation($"Failed({context.Id}) to connect {addr}");
            // TODO: Add proper exception and reconnection handling
            return;
        }

        IPEndPoint localEndpoint = (IPEndPoint)client.LocalEndPoint!;
        IPEndPoint remoteEndpoint = (IPEndPoint)client.RemoteEndPoint!;

        context.RemoteEndpoint = Multiaddr.From(
            ipProtocol,
            ipProtocol == MultiaddrEnum.Ip4 ? remoteEndpoint.Address.MapToIPv4() : remoteEndpoint.Address.MapToIPv6(),
            MultiaddrEnum.Tcp, remoteEndpoint.Port);
        context.LocalEndpoint = Multiaddr.From(
            ipProtocol,
            ipProtocol == MultiaddrEnum.Ip4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6(),
            MultiaddrEnum.Tcp, localEndpoint.Port);
        context.LocalPeer.Address = context.LocalEndpoint.Append(MultiaddrEnum.P2p, context.LocalPeer.Identity.PeerId.ToString());

        IChannel upChannel = channelFactory.SubDial(context);
        channel.Token.Register(() => upChannel.CloseAsync());
        //upChannel.OnClosing += (graceful) => upChannel.CloseAsync(graceful);

        Task receiveTask = Task.Run(async () =>
        {
            byte[] buf = new byte[client.ReceiveBufferSize];
            try
            {
                while (!upChannel.IsClosed)
                {
                    int len = await client.ReceiveAsync(buf, SocketFlags.None);
                    if (len != 0)
                    {
                        _logger?.LogDebug("Receive {0} data, len={1}", context.Id, len);
                        await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf[..len]));
                    }
                }

                waitForStop.SetCanceled();
            }
            catch (SocketException)
            {
                await upChannel.CloseAsync();
                waitForStop.SetCanceled();
            }
        });

        Task sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    await client.SendAsync(data.ToArray(), SocketFlags.None);
                }

                waitForStop.SetCanceled();
            }
            catch (SocketException)
            {
                await upChannel.CloseAsync(false);
                waitForStop.SetCanceled();
            }
        });

        await Task.WhenAll(receiveTask, sendTask);
    }
}
