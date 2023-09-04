// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
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

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("ListenAsync({contextId})", context.Id);

        Socket srv = new(SocketType.Stream, ProtocolType.Tcp);
        Multiaddr addr = context.LocalPeer.Address;
        Core.Enums.Multiaddr ipProtocol = addr.Has(Core.Enums.Multiaddr.Ip4) ? Core.Enums.Multiaddr.Ip4 : Core.Enums.Multiaddr.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int tcpPort = int.Parse(addr.At(Core.Enums.Multiaddr.Tcp)!);
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen(32767);

        IPEndPoint localIpEndpoint = (IPEndPoint)srv.LocalEndPoint!;
        channel.OnClose(() =>
        {
            srv.Close();
            return Task.CompletedTask;
        });
        Core.Enums.Multiaddr newIpProtocol = localIpEndpoint.AddressFamily == AddressFamily.InterNetwork
            ? Core.Enums.Multiaddr.Ip4
            : Core.Enums.Multiaddr.Ip6;

        context.LocalEndpoint = Core.Multiaddr.From(newIpProtocol, localIpEndpoint.Address.ToString(),
            Core.Enums.Multiaddr.Tcp,
            localIpEndpoint.Port);

        context.LocalPeer.Address = context.LocalPeer.Address.Replace(
                context.LocalEndpoint.Has(Core.Enums.Multiaddr.Ip4) ? Core.Enums.Multiaddr.Ip4 : Core.Enums.Multiaddr.Ip6, newIpProtocol,
                localIpEndpoint.Address.ToString())
            .Replace(
                Core.Enums.Multiaddr.Tcp,
                localIpEndpoint.Port.ToString());

        await Task.Run(async () =>
        {
            while (!channel.IsClosed)
            {
                Socket client = await srv.AcceptAsync();
                IPeerContext clientContext = context.Fork();
                IPEndPoint remoteIpEndpoint = (IPEndPoint)client.RemoteEndPoint!;

                clientContext.RemoteEndpoint = Core.Multiaddr.From(
                    remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork
                        ? Core.Enums.Multiaddr.Ip4
                        : Core.Enums.Multiaddr.Ip6, remoteIpEndpoint.Address.ToString(), Core.Enums.Multiaddr.Tcp,
                    remoteIpEndpoint.Port);
                clientContext.LocalPeer.Address = context.LocalPeer.Address.Replace(
                        context.LocalEndpoint.Has(Core.Enums.Multiaddr.Ip4) ? Core.Enums.Multiaddr.Ip4 : Core.Enums.Multiaddr.Ip6, newIpProtocol,
                        localIpEndpoint.Address.ToString())
                    .Replace(
                        Core.Enums.Multiaddr.Tcp,
                        remoteIpEndpoint.Port.ToString());
                clientContext.RemotePeer.Address = new Multiaddr()
                    .Append(remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork
                        ? Core.Enums.Multiaddr.Ip4
                        : Core.Enums.Multiaddr.Ip6, remoteIpEndpoint.Address.ToString())
                    .Append(Core.Enums.Multiaddr.Tcp, remoteIpEndpoint.Port.ToString());

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
        Core.Enums.Multiaddr ipProtocol = addr.Has(Core.Enums.Multiaddr.Ip4) ? Core.Enums.Multiaddr.Ip4 : Core.Enums.Multiaddr.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int tcpPort = int.Parse(addr.At(Core.Enums.Multiaddr.Tcp)!);
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

        context.RemoteEndpoint = Core.Multiaddr.From(
            ipProtocol,
            ipProtocol == Core.Enums.Multiaddr.Ip4 ? remoteEndpoint.Address.MapToIPv4() : remoteEndpoint.Address.MapToIPv6(),
            Core.Enums.Multiaddr.Tcp, remoteEndpoint.Port);
        context.LocalEndpoint = Core.Multiaddr.From(
            ipProtocol,
            ipProtocol == Core.Enums.Multiaddr.Ip4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6(),
            Core.Enums.Multiaddr.Tcp, localEndpoint.Port);
        context.LocalPeer.Address = context.LocalEndpoint.Append(Core.Enums.Multiaddr.P2p, context.LocalPeer.Identity.PeerId.ToString());

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
