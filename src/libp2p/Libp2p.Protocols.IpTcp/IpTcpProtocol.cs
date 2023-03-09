using System.Net;
using System.Net.Sockets;
using Libp2p.Core;
using Libp2p.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Libp2p.Protocols;

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
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol));
        int tcpPort = int.Parse(addr.At(Multiaddr.Tcp));
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen();

        channel.OnClose(async () => { await srv.DisconnectAsync(false); });
        Multiaddr newIpProtocol = (srv.LocalEndPoint as IPEndPoint).AddressFamily == AddressFamily.InterNetwork
            ? Multiaddr.Ip4
            : Multiaddr.Ip6;

        context.LocalEndpoint = MultiAddr.From(newIpProtocol, (srv.LocalEndPoint as IPEndPoint).Address.ToString(),
            Multiaddr.Tcp,
            (srv.LocalEndPoint as IPEndPoint).Port);

        context.LocalPeer.Address = context.LocalPeer.Address.Replace(
                context.LocalEndpoint.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6, newIpProtocol,
                (srv.LocalEndPoint as IPEndPoint).Address.ToString())
            .Replace(
                Multiaddr.Tcp,
                (srv.LocalEndPoint as IPEndPoint).Port.ToString());

        Task.Run(async () =>
        {
            while (true)
            {
                Socket client = await srv.AcceptAsync(channel.Token);
                IPeerContext clientContext = context.Fork();
                clientContext.RemoteEndpoint = MultiAddr.From(
                    (client.RemoteEndPoint as IPEndPoint).AddressFamily == AddressFamily.InterNetwork
                        ? Multiaddr.Ip4
                        : Multiaddr.Ip6, (client.RemoteEndPoint as IPEndPoint).Address.ToString(), Multiaddr.Tcp,
                    (client.RemoteEndPoint as IPEndPoint).Port);
                IChannel chan = channelFactory.SubListen(clientContext);
                byte[] buf = new byte[client.ReceiveBufferSize];
                Task.Run(async () =>
                {
                    try
                    {
                        while (!chan.IsClosed)
                        {
                            client.Poll(TimeSpan.FromSeconds(10), SelectMode.SelectRead);
                            int len = await client.ReceiveAsync(buf);
                            if (len != 0)
                            {
                                await chan.Writer.WriteAsync(buf[..len]);
                            }
                            else
                            {
                                _logger?.LogDebug("Spin");
                            }
                        }
                    }
                    catch (SocketException e)
                    {
                        await chan.CloseAsync();
                    }
                }, chan.Token);
                byte[] inbuf = new byte[client.SendBufferSize];
                Task.Run(async () =>
                {
                    try
                    {
                        while (!chan.IsClosed)
                        {
                            int len = await chan.Reader.ReadAsync(inbuf, false);
                            await client.SendAsync(inbuf[..len]);
                        }
                    }
                    catch (SocketException e)
                    {
                        await chan.CloseAsync();
                    }
                }, chan.Token);
            }
        });
    }

    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory, IPeerContext context)
    {
        TaskCompletionSource<bool?> waitForStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        MultiAddr addr = context.RemotePeer.Address;
        Multiaddr ipProtocol = addr.Has(Multiaddr.Ip4) ? Multiaddr.Ip4 : Multiaddr.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol));
        int tcpPort = int.Parse(addr.At(Multiaddr.Tcp));
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

        IPEndPoint localEndpoint = client.LocalEndPoint as IPEndPoint;
        IPEndPoint remoteEndpoint = client.RemoteEndPoint as IPEndPoint;

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

        Task.Run(async () =>
        {
            byte[] buf = new byte[client.ReceiveBufferSize];
            try
            {
                while (!chan.IsClosed)
                {
                    client.Poll(TimeSpan.FromSeconds(10), SelectMode.SelectRead);
                    int len = await client.ReceiveAsync(buf);
                    if (len != 0)
                    {
                        _logger?.LogDebug("Receive data, len={0}", len);
                        await chan.Writer.WriteAsync(buf[..len]);
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

        Task.Run(async () =>
        {
            byte[] inbuf = new byte[client.SendBufferSize];
            try
            {
                while (!chan.IsClosed)
                {
                    int len = await chan.Reader.ReadAsync(inbuf, false);
                    _logger?.LogDebug("Send data, len={0}", len);
                    await client.SendAsync(inbuf[..len]);
                }

                waitForStop.SetCanceled();
            }
            catch (SocketException e)
            {
                await chan.CloseAsync();
                waitForStop.SetCanceled();
            }
        });
    }
}
