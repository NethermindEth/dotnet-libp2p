// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Enums;
using Microsoft.Extensions.Logging;

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

    public async Task ListenAsync(IChannel channel, IChannelFactory channelFactory, IPeerContext context)
    {
        Socket srv = new(SocketType.Stream, ProtocolType.Tcp);
        MultiAddr addr = context.LocalPeer.Address;
        Multiaddr ipProtocol = addr.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int tcpPort = int.Parse(addr.At(Multiaddr.Tcp)!);
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen(32767);

        IPEndPoint localIpEndpoint = (IPEndPoint)srv.LocalEndPoint!;
        channel.OnClose(() =>
        {
            srv.Disconnect(false);
            return Task.CompletedTask;
        });
        Multiaddr newIpProtocol = localIpEndpoint.AddressFamily == AddressFamily.InterNetwork
            ? Multiaddr.Ip4
            : Multiaddr.Ip6;

        context.LocalEndpoint = MultiAddr.From(newIpProtocol, localIpEndpoint.Address.ToString(),
            Multiaddr.Tcp,
            localIpEndpoint.Port);

        context.LocalPeer.Address = context.LocalPeer.Address.Replace(
                context.LocalEndpoint.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6, newIpProtocol,
                localIpEndpoint.Address.ToString())
            .Replace(
                Multiaddr.Tcp,
                localIpEndpoint.Port.ToString());

        _ = Task.Run(async () =>
        {
            while (!channel.IsClosed)
            {
                Socket client = await srv.AcceptAsync();
                IPeerContext clientContext = context.Fork();
                IPEndPoint remoteIpEndpoint = (IPEndPoint)client.RemoteEndPoint!;

                clientContext.RemoteEndpoint = MultiAddr.From(
                    remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork
                        ? Multiaddr.Ip4
                        : Multiaddr.Ip6, remoteIpEndpoint.Address.ToString(), Multiaddr.Tcp,
                    remoteIpEndpoint.Port);
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
                                await chan.Writer.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
                            }
                        }
                    }
                    catch (SocketException e)
                    {
                        await chan.CloseAsync(false);
                    }
                }, chan.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ReadOnlySequence<byte> data in chan.Reader.ReadAllAsync())
                        {
                            await client.SendAsync(data.ToArray(), SocketFlags.None);
                        }
                    }
                    catch (SocketException e)
                    {
                        await chan.CloseAsync(false);
                    }
                }, chan.Token);
            }
        });
    }

    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory, IPeerContext context)
    {
        TaskCompletionSource<bool?> waitForStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        MultiAddr addr = context.RemotePeer.Address!;
        Multiaddr ipProtocol = addr.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int tcpPort = int.Parse(addr.At(Multiaddr.Tcp)!);
        try
        {
            await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort));
        }
        catch (SocketException se)
        {
            _logger?.LogInformation("Failed to connect");
            // TODO: Add proper exception and reconnection handling
            return;
        }

        IPEndPoint localEndpoint = (IPEndPoint)client.LocalEndPoint!;
        IPEndPoint remoteEndpoint = (IPEndPoint)client.RemoteEndPoint!;

        context.RemoteEndpoint = MultiAddr.From(
            ipProtocol,
            ipProtocol == Multiaddr.Ip4 ? remoteEndpoint.Address.MapToIPv4() : remoteEndpoint.Address.MapToIPv6(),
            Multiaddr.Tcp, remoteEndpoint.Port);
        context.LocalEndpoint = MultiAddr.From(
            ipProtocol,
            ipProtocol == Multiaddr.Ip4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6(),
            Multiaddr.Tcp, localEndpoint.Port);
        context.LocalPeer.Address = context.LocalEndpoint.Append(Multiaddr.P2p, context.LocalPeer.Identity.PeerId);

        IChannel chan = channelFactory.SubDial(context);

        _ = Task.Run(async () =>
        {
            byte[] buf = new byte[client.ReceiveBufferSize];
            try
            {
                while (!chan.IsClosed)
                {
                    int len = await client.ReceiveAsync(buf, SocketFlags.None);
                    if (len != 0)
                    {
                        _logger?.LogDebug("Receive data, len={0}", len);
                        await chan.Writer.WriteAsync(new ReadOnlySequence<byte>(buf[..len]));
                    }
                }

                waitForStop.SetCanceled();
            }
            catch (SocketException)
            {
                await chan.CloseAsync();
                waitForStop.SetCanceled();
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in chan.Reader.ReadAllAsync())
                {
                    await client.SendAsync(data.ToArray(), SocketFlags.None);
                }

                waitForStop.SetCanceled();
            }
            catch (SocketException e)
            {
                await chan.CloseAsync(false);
                waitForStop.SetCanceled();
            }
        });
    }
}
