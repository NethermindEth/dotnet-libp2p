// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
using System.Runtime.CompilerServices;
using System.Diagnostics;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.Pubsub.E2eTests")]

namespace Nethermind.Libp2p.Protocols;

public class IpTcpProtocol(ILoggerFactory? loggerFactory = null) : ITransportProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();
    private static Multiaddress ToTcpMultiAddress(IPAddress a, PeerId peerId) => Multiaddress.Decode($"/{(a.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6")}/{a}/tcp/0/p2p/{peerId}");


    public string Id => "ip-tcp";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [.. IpHelper.GetListenerAddresses().Select(a => ToTcpMultiAddress(a, peerId))];

    public static bool IsAddressMatch(Multiaddress addr) => addr.Has<TCP>();

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint endpoint = listenAddr.ToEndPoint();

        listener.Bind(endpoint);
        listener.Listen();

        context.Activity?.SetTag("listen-on-addr", endpoint.Address);
        context.Activity?.SetTag("listen-on-port", endpoint.Port);

        if (endpoint.Port is 0)
        {
            IPEndPoint localIpEndpoint = (IPEndPoint)listener.LocalEndPoint!;
            listenAddr.ReplaceOrAdd<TCP>(localIpEndpoint.Port);
        }

        token.Register(listener.Close);

        _logger?.LogDebug($"Ready to handle connections at {listenAddr}");
        context.ListenerReady(listenAddr);
        context.Activity?.AddEvent(new ActivityEvent("ready"));

        await Task.Run(async () =>
        {
            for (; ; )
            {
                Socket client = await listener.AcceptAsync();

                context.Activity?.AddEvent(new ActivityEvent($"connected {client.RemoteEndPoint}"));

                CancellationTokenSource internalCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                token = CancellationTokenSource.CreateLinkedTokenSource(token).Token;

#if DEBUG
                TriggerDisconnection += localPeerId =>
                {
                    if (localPeerId is null || context.Peer.Identity.PeerId == localPeerId)
                    {
                        _logger?.LogDebug("Triggering disconnection of incoming connection");
                        client.Close();
                        internalCts.Cancel();
                    }
                };
#endif
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
                    catch (SocketException)
                    {
                        connectionCtx.Activity?.SetStatus(ActivityStatusCode.Error);
                        connectionCtx.Activity?.AddEvent(new ActivityEvent("disconnected due to a socket exception"));
                        _ = upChannel.CloseAsync();
                    }
                });

                Task writeTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync(token))
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
                        connectionCtx.Activity?.SetStatus(ActivityStatusCode.Error);
                        connectionCtx.Activity?.AddEvent(new ActivityEvent("disconnected due to a socket exception"));
                    }
                });

                _ = Task.WhenAny(readTask, writeTask).ContinueWith((t) => { _ = upChannel.CloseAsync(); connectionCtx.Dispose(); });
            }
        }, token);
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);

        CancellationTokenSource internalCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = CancellationTokenSource.CreateLinkedTokenSource(token).Token;

#if DEBUG
        TriggerDisconnection += localPeerId =>
        {
            if (localPeerId is null || context.Peer.Identity.PeerId == localPeerId)
            {
                _logger?.LogDebug("Triggering disconnection of outgoing connection");
                client.Close();
                internalCts.Cancel();
            }
        };
#endif

        IPEndPoint remoteEndpoint = remoteAddr.ToEndPoint();
        _logger?.LogDebug("Dialling {0}:{1}", remoteEndpoint.Address, remoteEndpoint.Port);

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
                while (client.Connected)
                {
                    byte[] buf = new byte[client.ReceiveBufferSize];
                    int dataLength = await client.ReceiveAsync(buf, SocketFlags.None);
                    _logger?.LogDebug("Ctx({0}): receive, length={1}", connectionCtx.Id, dataLength);

                    if (dataLength == 0 || (await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf[..dataLength]))) != IOResult.Ok)
                    {
                        break;
                    }
                }
                _logger?.LogDebug("Ctx({0}): end receiving", connectionCtx.Id);
            }
            catch (SocketException e)
            {
                _logger?.LogDebug("Ctx({0}): end receiving, socket exception {1}", connectionCtx.Id, e.Message);
            }
        });

        Task sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    _logger?.LogDebug("Ctx({0}): send, length={2}", connectionCtx.Id, data.Length);
                    int sent = await client.SendAsync(data.ToArray(), SocketFlags.None);
                    if (sent is 0 || !client.Connected)
                    {
                        break;
                    }
                }

                _logger?.LogDebug("Ctx({0}): end sending", connectionCtx.Id);
            }
            catch (SocketException e)
            {
                _logger?.LogDebug("Ctx({0}): end sending, socket exception {1}", connectionCtx.Id, e.Message);
            }
            finally
            {
                client.Close();
            }
        });

        await Task.WhenAny(receiveTask, sendTask).ContinueWith(t => connectionCtx.Dispose());

        _ = upChannel.CloseAsync();
        _logger?.LogDebug("Ctx({0}): dialling ended", connectionCtx.Id);
    }

#if DEBUG
    public static TriggerDisconnectionEvent TriggerDisconnection = (_) => { };

    public delegate void TriggerDisconnectionEvent(PeerId? localPeerId = null);
#endif
}
