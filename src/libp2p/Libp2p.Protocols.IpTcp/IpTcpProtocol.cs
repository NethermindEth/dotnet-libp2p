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

public class IpTcpProtocol(ILoggerFactory? loggerFactory = null) : IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();

    public string Id => "ip-tcp";

    public async Task ListenAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        if (channelFactory is null)
        {
            throw new Exception("Protocol is not properly instantiated");
        }

        Multiaddress addr = context.LocalPeer.Address;
        bool isIP4 = addr.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = int.Parse(addr.Get<TCP>().ToString());

        Socket srv = new(SocketType.Stream, ProtocolType.Tcp);
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen(tcpPort);
        singalingChannel.GetAwaiter().OnCompleted(() =>
        {
            srv.Close();
        });

        IPEndPoint localIpEndpoint = (IPEndPoint)srv.LocalEndPoint!;


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
            for (; ; )
            {
                Socket client = await srv.AcceptAsync();
                IPeerContext clientContext = context.Fork();
                IPEndPoint remoteIpEndpoint = (IPEndPoint)client.RemoteEndPoint!;

                Multiaddress remoteMultiaddress = new();
                remoteMultiaddress = isIP4 ? remoteMultiaddress.Add<IP4>(remoteIpEndpoint.Address.MapToIPv4()) : remoteMultiaddress.Add<IP6>(remoteIpEndpoint.Address.MapToIPv6());
                remoteMultiaddress = remoteMultiaddress.Add<TCP>(remoteIpEndpoint.Port);

                clientContext.RemoteEndpoint = clientContext.RemotePeer.Address = remoteMultiaddress;

                IChannel upChannel = channelFactory.SubListen(clientContext);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (; ; )
                        {
                            if (client.Available == 0)
                            {
                                await Task.Yield();
                            }

                            byte[] buf = new byte[1024];
                            int length = await client.ReceiveAsync(buf, SocketFlags.None);
                            if (length != 0)
                            {
                                if ((await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..length]))) != IOResult.Ok)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch (SocketException e)
                    {
                        await upChannel.CloseAsync();
                    }
                });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                        {
                            await client.SendAsync(data.ToArray(), SocketFlags.None);
                        }
                    }
                    catch (SocketException)
                    {
                        _logger?.LogInformation($"Disconnected({context.Id}) due to a socket exception");
                        await upChannel.CloseAsync();
                    }
                });
            }
        });
    }

    public async Task DialAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        if (channelFactory is null)
        {
            throw new ProtocolViolationException();
        }

        Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        Multiaddress addr = context.RemotePeer.Address;
        MultiaddressProtocol ipProtocol = addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = addr.Get<TCP>().Port;

        _logger?.LogDebug("Dialing {0}:{1}", ipAddress, tcpPort);

        try
        {
            await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort));
            singalingChannel.GetAwaiter().OnCompleted(() =>
            {
                client.Close();
            });
        }
        catch (SocketException e)
        {
            _logger?.LogDebug($"Failed({context.Id}) to connect {addr}");
            _logger?.LogTrace($"Failed with {e.GetType()}: {e.Message}");
            _ = singalingChannel.CloseAsync();
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

        Task receiveTask = Task.Run(async () =>
        {
            byte[] buf = new byte[client.ReceiveBufferSize];
            try
            {
                for (; ; )
                {
                    int dataLength = await client.ReceiveAsync(buf, SocketFlags.None);
                    if (dataLength != 0)
                    {
                        _logger?.LogDebug("Receive {0} data, len={1}", context.Id, dataLength);
                        if ((await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf[..dataLength]))) != IOResult.Ok)
                        {
                            break;
                        };
                    }
                }

            }
            catch (SocketException)
            {
                _ = upChannel.CloseAsync();
            }
        });

        Task sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    _logger?.LogDebug("Send {0} data, len={1}", context.Id, data.Length);
                    await client.SendAsync(data.ToArray(), SocketFlags.None);
                }
            }
            catch (SocketException)
            {
                _ = upChannel.CloseAsync();
            }
        });

        await Task.WhenAll(receiveTask, sendTask);
        _ = upChannel.CloseAsync();
    }
}
