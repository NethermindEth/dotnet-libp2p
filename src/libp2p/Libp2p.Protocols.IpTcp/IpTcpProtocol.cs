// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;

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

        Multiaddress addr = context.LocalPeer.Address;
        bool isIP4 = addr.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = int.Parse(addr.Get<TCP>().ToString());

        Socket srv = new(SocketType.Stream, ProtocolType.Tcp);
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen(tcpPort);

        IPEndPoint localIpEndpoint = (IPEndPoint)srv.LocalEndPoint!;
        channel.OnClose(() =>
        {
            srv.Close();
            return Task.CompletedTask;
        });

        Multiaddress localMultiaddress = new();
        localMultiaddress = isIP4 ? localMultiaddress.Add<IP4>(localIpEndpoint.Address.MapToIPv4()) : localMultiaddress.Add<IP6>(localIpEndpoint.Address.MapToIPv6());
        localMultiaddress = localMultiaddress.Add<TCP>(localIpEndpoint.Port);
        context.LocalEndpoint = localMultiaddress;

        if (tcpPort == 0)
        {
            context.LocalPeer.Address = context.LocalPeer.Address
                .ReplaceOrAdd<TCP>(localIpEndpoint.Port);
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

                Multiaddress remoteMultiaddress = new();
                remoteMultiaddress = isIP4 ? remoteMultiaddress.Add<IP4>(remoteIpEndpoint.Address.MapToIPv4()) : remoteMultiaddress.Add<IP6>(remoteIpEndpoint.Address.MapToIPv6());
                remoteMultiaddress = remoteMultiaddress.Add<TCP>(remoteIpEndpoint.Port);

                clientContext.RemoteEndpoint = clientContext.RemotePeer.Address = remoteMultiaddress;

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

                            byte[] buf = new byte[1024];
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
        Multiaddress addr = context.RemotePeer.Address;
        MultiaddressProtocol ipProtocol = addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = addr.Get<TCP>().Port;
        try
        {
            await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort), channel.Token);
        }
        catch (SocketException e)
        {
            _logger?.LogInformation($"Failed({context.Id}) to connect {addr}");
            _logger?.LogTrace($"Failed with {e.GetType()}: {e.Message}");
            // TODO: Add proper exception and reconnection handling
            return;
        }

        IPEndPoint localEndpoint = (IPEndPoint)client.LocalEndPoint!;
        IPEndPoint remoteEndpoint = (IPEndPoint)client.RemoteEndPoint!;

        var isIP4 = addr.Has<IP4>();

        var remoteMultiaddress = new Multiaddress();
        var remoteIpAddress = isIP4 ? remoteEndpoint.Address.MapToIPv4() : remoteEndpoint.Address.MapToIPv6();
        remoteMultiaddress = isIP4 ? remoteMultiaddress.Add<IP4>(remoteIpAddress) : remoteMultiaddress.Add<IP6>(remoteIpAddress);
        context.RemoteEndpoint = remoteMultiaddress.Add<TCP>(remoteEndpoint.Port);


        var localMultiaddress = new Multiaddress();
        var localIpAddress = isIP4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6();
        localMultiaddress = isIP4 ? localMultiaddress.Add<IP4>(localIpAddress) : localMultiaddress.Add<IP6>(localIpAddress);
        context.LocalEndpoint = localMultiaddress.Add<TCP>(localEndpoint.Port);

        context.LocalPeer.Address = context.LocalEndpoint.Add<P2P>(context.LocalPeer.Identity.PeerId.ToString());

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
