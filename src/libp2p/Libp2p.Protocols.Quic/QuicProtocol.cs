// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols;

// TODO: Rewrite with SocketAsyncEventArgs
public class QuicProtocol : IProtocol
{
    private readonly ILogger? _logger;

    public QuicProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<QuicProtocol>();
    }

    public string Id => "quic";

    public Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        throw new NotImplementedException();
        //MultiAddr addr = context.LocalPeer.Address;
        //Multiaddr ipProtocol = addr.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6;
        //IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        //int tcpPort = int.Parse(addr.At(Multiaddr.Udp)!);

        //// First, check if QUIC is supported.
        //if (!QuicConnection.IsSupported)
        //{
        //    Console.WriteLine("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        //    return;
        //}

        //var localEndpoint = new IPEndPoint(ipAddress, tcpPort);
        //// This represents the minimal configuration necessary to open a connection.
        //var clientConnectionOptions = new QuicClientConnectionOptions
        //{
        //    // End point of the server to connect to.
        //    LocalEndPoint = new IPEndPoint(ipAddress, tcpPort),

        //    // Used to abort stream if it's not properly closed by the user.
        //    // See https://www.rfc-editor.org/rfc/rfc9000#section-20.2
        //    DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.

        //    // Used to close the connection if it's not done by the user.
        //    // See https://www.rfc-editor.org/rfc/rfc9000#section-20.2
        //    DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

        //    // Optionally set limits for inbound streams.
        //    MaxInboundUnidirectionalStreams = 10,
        //    MaxInboundBidirectionalStreams = 100,

        //    // Same options as for client side SslStream.
        //    ClientAuthenticationOptions = new SslClientAuthenticationOptions
        //    {
        //        // List of supported application protocols.
        //        ApplicationProtocols = channelFactory.SubProtocols.Select(proto => new SslApplicationProtocol(proto.Id)).ToList()

        //    }
        //};


        //Multiaddr newIpProtocol = localEndpoint.AddressFamily == AddressFamily.InterNetwork
        //    ? Multiaddr.Ip4
        //    : Multiaddr.Ip6;

        //context.LocalEndpoint = MultiAddr.From(newIpProtocol, localEndpoint.Address.ToString(),
        //    Multiaddr.Udp,
        //    localEndpoint.Port);

        //context.LocalPeer.Address = context.LocalPeer.Address.Replace(
        //        context.LocalEndpoint.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6, newIpProtocol,
        //        localEndpoint.Address.ToString())
        //    .Replace(
        //        Multiaddr.Udp,
        //        localEndpoint.Port.ToString());


        //// Initialize, configure and connect to the server.
        //var connection = await QuicConnection.ConnectAsync(clientConnectionOptions);

        //channel.OnClose(async () =>
        //{
        //    await connection.CloseAsync(0);
        //    await connection.DisposeAsync();
        //});

        //Console.WriteLine($"Connected {connection.LocalEndPoint} --> {connection.RemoteEndPoint}");

        //_ = Task.Run(async () =>
        //{
        //    while (!channel.IsClosed)
        //    {
        //        var incomingStream = await connection.AcceptInboundStreamAsync();
        //        IPeerContext clientContext = context.Fork();
        //        IPEndPoint remoteIpEndpoint = (IPEndPoint)client.RemoteEndPoint!;

        //        clientContext.RemoteEndpoint = MultiAddr.From(
        //            remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork
        //                ? Multiaddr.Ip4
        //                : Multiaddr.Ip6, remoteIpEndpoint.Address.ToString(), Multiaddr.Tcp,
        //            remoteIpEndpoint.Port);
        //        clientContext.LocalPeer.Address = context.LocalPeer.Address.Replace(
        //                context.LocalEndpoint.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6, newIpProtocol,
        //                remoteIpEndpoint.Address.ToString())
        //            .Replace(
        //                Multiaddr.Tcp,
        //                remoteIpEndpoint.Port.ToString());
        //        clientContext.RemotePeer.Address = new MultiAddr()
        //            .Append(remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork
        //                ? Multiaddr.Ip4
        //                : Multiaddr.Ip6, remoteIpEndpoint.Address.ToString())
        //            .Append(Multiaddr.Tcp, remoteIpEndpoint.Port.ToString());

        //        IChannel chan = channelFactory.SubListen(clientContext);

        //        _ = Task.Run(async () =>
        //        {
        //            try
        //            {
        //                while (!chan.IsClosed)
        //                {
        //                    if (client.Available == 0)
        //                    {
        //                        await Task.Yield();
        //                    }

        //                    byte[] buf = new byte[client.Available];
        //                    int len = await client.ReceiveAsync(buf, SocketFlags.None);
        //                    if (len != 0)
        //                    {
        //                        await chan.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
        //                    }
        //                }
        //            }
        //            catch (SocketException)
        //            {
        //                await chan.CloseAsync(false);
        //            }
        //        }, chan.Token);
        //        _ = Task.Run(async () =>
        //        {
        //            try
        //            {
        //                await foreach (ReadOnlySequence<byte> data in chan.ReadAllAsync())
        //                {
        //                    await client.SendAsync(data.ToArray(), SocketFlags.None);
        //                }
        //            }
        //            catch (SocketException)
        //            {
        //                _logger?.LogInformation("Disconnected due to a socket exception");
        //                await chan.CloseAsync(false);
        //            }
        //        }, chan.Token);
        //    }
        //});
    }

    public Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        throw new NotImplementedException();
        //// First, check if QUIC is supported.
        //if (!QuicConnection.IsSupported)
        //{
        //    Console.WriteLine("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        //    return;
        //}

        //// This represents the minimal configuration necessary to open a connection.
        //var clientConnectionOptions = new QuicClientConnectionOptions
        //{
        //    // End point of the server to connect to.
        //    RemoteEndPoint = listener.LocalEndPoint,

        //    // Used to abort stream if it's not properly closed by the user.
        //    // See https://www.rfc-editor.org/rfc/rfc9000#section-20.2
        //    DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.

        //    // Used to close the connection if it's not done by the user.
        //    // See https://www.rfc-editor.org/rfc/rfc9000#section-20.2
        //    DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

        //    // Optionally set limits for inbound streams.
        //    MaxInboundUnidirectionalStreams = 10,
        //    MaxInboundBidirectionalStreams = 100,

        //    // Same options as for client side SslStream.
        //    ClientAuthenticationOptions = new SslClientAuthenticationOptions
        //    {
        //        // List of supported application protocols.
        //        ApplicationProtocols = channelFactory.SubProtocols.Select(proto => new SslApplicationProtocol(proto.Id)).ToList()
        //    }
        //};

        //// Initialize, configure and connect to the server.
        //var connection = await QuicConnection.ConnectAsync(clientConnectionOptions);

        //Console.WriteLine($"Connected {connection.LocalEndPoint} --> {connection.RemoteEndPoint}");

        //// Open a bidirectional (can both read and write) outbound stream.
        //var outgoingStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

        //// Work with the outgoing stream ...

        //// To accept any stream on a client connection, at least one of MaxInboundBidirectionalStreams or MaxInboundUnidirectionalStreams of QuicConnectionOptions must be set.
        //while (isRunning)
        //{
        //    // Accept an inbound stream.
        //    var incomingStream = await connection.AcceptInboundStreamAsync();

        //    // Work with the incoming stream ...
        //}

        //// Close the connection with the custom code.
        //await connection.CloseAsync(0x0C);

        //// Dispose the connection.
        //await connection.DisposeAsync();


        //TaskCompletionSource<bool?> waitForStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        //Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        //MultiAddr addr = context.RemotePeer.Address;
        //Multiaddr ipProtocol = addr.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6;
        //IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        //int tcpPort = int.Parse(addr.At(Multiaddr.Tcp)!);
        //try
        //{
        //    await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort));
        //}
        //catch (SocketException)
        //{
        //    _logger?.LogInformation("Failed to connect");
        //    // TODO: Add proper exception and reconnection handling
        //    return;
        //}

        //IPEndPoint localEndpoint = (IPEndPoint)client.LocalEndPoint!;
        //IPEndPoint remoteEndpoint = (IPEndPoint)client.RemoteEndPoint!;

        //context.RemoteEndpoint = MultiAddr.From(
        //    ipProtocol,
        //    ipProtocol == Multiaddr.Ip4 ? remoteEndpoint.Address.MapToIPv4() : remoteEndpoint.Address.MapToIPv6(),
        //    Multiaddr.Tcp, remoteEndpoint.Port);
        //context.LocalEndpoint = MultiAddr.From(
        //    ipProtocol,
        //    ipProtocol == Multiaddr.Ip4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6(),
        //    Multiaddr.Tcp, localEndpoint.Port);
        //context.LocalPeer.Address = context.LocalEndpoint.Append(Multiaddr.P2p, context.LocalPeer.Identity.PeerId);

        //IChannel upChannel = channelFactory.SubDial(context);
        ////upChannel.OnClosing += (graceful) => upChannel.CloseAsync(graceful);

        //_ = Task.Run(async () =>
        //{
        //    byte[] buf = new byte[client.ReceiveBufferSize];
        //    try
        //    {
        //        while (!upChannel.IsClosed)
        //        {
        //            int len = await client.ReceiveAsync(buf, SocketFlags.None);
        //            if (len != 0)
        //            {
        //                _logger?.LogDebug("Receive data, len={0}", len);
        //                await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf[..len]));
        //            }
        //        }

        //        waitForStop.SetCanceled();
        //    }
        //    catch (SocketException)
        //    {
        //        await upChannel.CloseAsync();
        //        waitForStop.SetCanceled();
        //    }
        //});

        //_ = Task.Run(async () =>
        //{
        //    try
        //    {
        //        await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
        //        {
        //            await client.SendAsync(data.ToArray(), SocketFlags.None);
        //        }

        //        waitForStop.SetCanceled();
        //    }
        //    catch (SocketException)
        //    {
        //        await upChannel.CloseAsync(false);
        //        waitForStop.SetCanceled();
        //    }
        //});
    }
}
