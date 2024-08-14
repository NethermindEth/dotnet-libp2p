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

public class IpTcpProtocol(ILoggerFactory? loggerFactory = null) : ITransportProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();

    public string Id => "ip-tcp";

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        Socket listener = new(SocketType.Stream, ProtocolType.Tcp);

        IPEndPoint endpoint = ToEndPoint(listenAddr);

        listener.Bind(endpoint);
        listener.Listen();

        if (endpoint.Port is 0)
        {
            IPEndPoint localIpEndpoint = (IPEndPoint)listener.LocalEndPoint!;
            listenAddr.Add<TCP>(localIpEndpoint.Port);
        }

        token.Register(listener.Close);


        _logger?.LogDebug("Ready to handle connections");
        context.ListenerReady(listenAddr);

        await Task.Run(async () =>
        {
            for (; ; )
            {
                Socket client = await listener.AcceptAsync();

                ITransportConnectionContext connectionCtx = context.CreateConnection();
                connectionCtx.Token.Register(client.Close);

                IChannel upChannel = connectionCtx.Upgrade();

                Task readTask = Task.Run(async () =>
                {
                    try
                    {
                        for (; client.Connected;)
                        {
                            if (client.Available == 0)
                            {
                                await Task.Yield();
                            }

                            byte[] buf = new byte[client.ReceiveBufferSize];
                            int length = await client.ReceiveAsync(buf, SocketFlags.None);

                            if (length is 0 || await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..length])) != IOResult.Ok)
                            {
                                break;
                            }
                        }
                    }
                    catch (SocketException e)
                    {
                        await upChannel.CloseAsync();
                    }
                });

                Task writeTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                        {
                            int sent = await client.SendAsync(data.ToArray(), SocketFlags.None);
                            if (sent is 0 || !client.Connected)
                            {
                                await upChannel.CloseAsync();
                                break;
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        _logger?.LogInformation($"Disconnected({context.Id}) due to a socket exception");
                        await upChannel.CloseAsync();
                    }
                });

                _ = Task.WhenAll(readTask, writeTask).ContinueWith(_ => connectionCtx.Dispose());
            }
        });
    }

    public async Task DialAsync(ITransportConnectionContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);

        IPEndPoint remoteEndpoint = ToEndPoint(remoteAddr);
        _logger?.LogDebug("Dialing {0}:{1}", remoteEndpoint.Address, remoteEndpoint.Port);

        try
        {
            await client.ConnectAsync(remoteEndpoint, token);
        }
        catch (SocketException e)
        {
            _logger?.LogDebug($"Failed({context.Id}) to connect {remoteAddr}");
            _logger?.LogTrace($"Failed with {e.GetType()}: {e.Message}");
            return;
        }

        context.Token.Register(client.Close);
        token.Register(client.Close);

        IChannel upChannel = context.Upgrade();

        Task receiveTask = Task.Run(async () =>
        {
            byte[] buf = new byte[client.ReceiveBufferSize];
            try
            {
                for (; client.Connected;)
                {
                    int dataLength = await client.ReceiveAsync(buf, SocketFlags.None);
                    _logger?.LogDebug("Receive {0} data, len={1}", context.Id, dataLength);

                    if (dataLength == 0 || (await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf[..dataLength]))) != IOResult.Ok)
                    {
                        break;
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
                    int sent = await client.SendAsync(data.ToArray(), SocketFlags.None);
                    if (sent is 0 || !client.Connected)
                    {
                        break;
                    }
                }
            }
            catch (SocketException)
            {
                _ = upChannel.CloseAsync();
            }
        });

        await Task.WhenAll(receiveTask, sendTask).ContinueWith(t => context.Dispose());

        _ = upChannel.CloseAsync();
    }

    private static IPEndPoint ToEndPoint(Multiaddress addr)
    {
        MultiaddressProtocol ipProtocol = addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = int.Parse(addr.Get<TCP>().ToString());
        return new IPEndPoint(ipAddress, tcpPort);
    }
}
