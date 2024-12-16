// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Utils;

namespace Nethermind.Libp2p.Protocols;

public class IpTcpProtocol(ILoggerFactory? loggerFactory = null) : ITransportProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();

    public string Id => "ip-tcp";
    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => IpHelper.GetListenerAddresses()
        .Select(a => Multiaddress.Decode($"/{(a.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6")}/{a}/tcp/0/p2p/{peerId}")).ToArray();
    public static bool IsAddressMatch(Multiaddress addr) => addr.Has<TCP>();

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        Socket listener = new(SocketType.Stream, ProtocolType.Tcp);

        IPEndPoint endpoint = listenAddr.ToEndPoint();

        listener.Bind(endpoint);
        listener.Listen();


        if (endpoint.Port is 0)
        {
            IPEndPoint localIpEndpoint = (IPEndPoint)listener.LocalEndPoint!;
            listenAddr.ReplaceOrAdd<TCP>(localIpEndpoint.Port);
        }

        token.Register(listener.Close);


        _logger?.LogDebug("Ready to handle connections");
        context.ListenerReady(listenAddr);

        await Task.Run(async () =>
        {
            for (; ; )
            {
                Socket client = await listener.AcceptAsync();

                INewConnectionContext connectionCtx = context.CreateConnection();
                connectionCtx.Token.Register(client.Close);
                connectionCtx.State.RemoteAddress = client.RemoteEndPoint.ToMultiaddress(ProtocolType.Tcp);

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
                    catch (SocketException e)
                    {
                        _logger?.LogInformation($"Disconnected due to a socket exception");
                        await upChannel.CloseAsync();
                    }
                });

                _ = Task.WhenAll(readTask, writeTask).ContinueWith(_ => connectionCtx.Dispose());
            }
        });
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);

        IPEndPoint remoteEndpoint = remoteAddr.ToEndPoint();
        _logger?.LogDebug("Dialing {0}:{1}", remoteEndpoint.Address, remoteEndpoint.Port);

        try
        {
            await client.ConnectAsync(remoteEndpoint, token);
        }
        catch (SocketException e)
        {
            _logger?.LogDebug($"Failed to connect {remoteAddr}");
            _logger?.LogTrace($"Failed with {e.GetType()}: {e.Message}");
            throw;
        }

        if (client.LocalEndPoint is null)
        {
            throw new Libp2pException($"{nameof(client.LocalEndPoint)} is not set for client connection.");
        }
        if (client.RemoteEndPoint is null)
        {
            throw new Libp2pException($"{nameof(client.RemoteEndPoint)} is not set for client connection.");
        }

        INewConnectionContext connectionCtx = context.CreateConnection();
        connectionCtx.State.RemoteAddress = client.RemoteEndPoint.ToMultiaddress(ProtocolType.Tcp);
        connectionCtx.State.LocalAddress = client.LocalEndPoint.ToMultiaddress(ProtocolType.Tcp);

        connectionCtx.Token.Register(client.Close);
        token.Register(client.Close);

        IChannel upChannel = connectionCtx.Upgrade();

        Task receiveTask = Task.Run(async () =>
        {
            try
            {
                for (; client.Connected;)
                {
                    byte[] buf = new byte[client.ReceiveBufferSize];
                    int dataLength = await client.ReceiveAsync(buf, SocketFlags.None);
                    _logger?.LogDebug("Ctx{0}: receive, length={1}", connectionCtx.Id, dataLength);

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
                    _logger?.LogDebug("Ctx{0}: send, length={2}", connectionCtx.Id, data.Length);
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
                return;
            }

            client.Close();
        });

        await Task.WhenAll(receiveTask, sendTask).ContinueWith(t => connectionCtx.Dispose());

        _ = upChannel.CloseAsync();
    }
}
