// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Utils;
using Nethermind.Libp2p.Protocols.AutoTls;
using MultiaddrWebSocket = Multiformats.Address.Protocols.WebSocket;
using SocketWebSocket = System.Net.WebSockets.WebSocket;

namespace Nethermind.Libp2p.Protocols;

public sealed class WebSocketProtocol(ILoggerFactory? loggerFactory = null, ITlsCertificateProvider? certificateProvider = null) : ITransportProtocol
{
    private const int BufferSize = 16 * 1024;
    private readonly ILogger<WebSocketProtocol>? _logger = loggerFactory?.CreateLogger<WebSocketProtocol>();

    public string Id => "websocket";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) =>
        [.. IpHelper.GetListenerAddresses().Select(a => ToWebSocketMultiAddress(a, peerId))];

    public static bool IsAddressMatch(Multiaddress addr) =>
        addr.Has<TCP>() && (addr.Has<MultiaddrWebSocket>() || addr.Has<WebSocketSecure>());

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        bool secure = listenAddr.Has<WebSocketSecure>();
        IPEndPoint endpoint = ToListenEndpoint(listenAddr);

        X509Certificate2? fallbackCertificate = null;
        if (secure)
        {
            fallbackCertificate = await GetCertificateAsync(token);
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebSocketProtocol).Assembly.FullName,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(endpoint.Address, endpoint.Port, listen =>
            {
                if (secure)
                {
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificateSelector = (_, _) => certificateProvider?.Current ?? fallbackCertificate;
                    });
                }
            });
        });

        await using WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Map("/{**path}", async httpContext =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using SocketWebSocket webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
            _logger?.LogDebug("Accepted WebSocket connection from {RemoteAddress}:{RemotePort}", httpContext.Connection.RemoteIpAddress, httpContext.Connection.RemotePort);
            INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = ToRemoteMultiAddress(httpContext, secure);
            IChannel upChannel = connection.Upgrade();

            try
            {
                await ExchangeAsync(webSocket, upChannel, _logger, connection.Token);
            }
            finally
            {
                await upChannel.CloseAsync();
                connection.Dispose();
            }
        });

        await app.StartAsync(token);
        listenAddr = ReplaceEphemeralPort(listenAddr, app);
        context.ListenerReady(listenAddr);

        _logger?.LogDebug("Ready to handle WebSocket connections at {Address}", listenAddr);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        using ClientWebSocket webSocket = new();
        webSocket.Options.Proxy = null;
        Uri uri = ToUri(remoteAddr);

        _logger?.LogDebug("Dialling WebSocket {Uri}", uri);
        await webSocket.ConnectAsync(uri, token);
        _logger?.LogDebug("Connected WebSocket {Uri}", uri);

        INewConnectionContext connection = context.CreateConnection();
        connection.State.RemoteAddress = remoteAddr;
        IChannel upChannel = connection.Upgrade();

        try
        {
            await ExchangeAsync(webSocket, upChannel, _logger, connection.Token);
        }
        finally
        {
            await upChannel.CloseAsync();
            connection.Dispose();
        }
    }

    private async Task<X509Certificate2> GetCertificateAsync(CancellationToken token)
    {
        if (certificateProvider is null)
        {
            throw new Libp2pSetupException("Secure WebSocket listeners require a registered ITlsCertificateProvider.");
        }

        return certificateProvider.Current ?? await certificateProvider.WaitForCertificateAsync(token);
    }

    private static async Task ExchangeAsync(SocketWebSocket webSocket, IChannel upChannel, ILogger? logger, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task readTask = ReadFromWebSocketAsync(webSocket, upChannel, logger, cts.Token);
        Task writeTask = WriteToWebSocketAsync(webSocket, upChannel, logger, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        await cts.CancelAsync();

        try
        {
            await Task.WhenAll(readTask, writeTask);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            await CloseWebSocketAsync(webSocket, CancellationToken.None);
        }
    }

    private static async Task ReadFromWebSocketAsync(SocketWebSocket webSocket, IChannel upChannel, ILogger? logger, CancellationToken token)
    {
        byte[] buffer = new byte[BufferSize];

        while (!token.IsCancellationRequested && webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer, token);
            if (result.MessageType is WebSocketMessageType.Close)
            {
                break;
            }

            if (result.Count is 0)
            {
                continue;
            }

            byte[] payload = buffer.AsSpan(0, result.Count).ToArray();
            logger?.LogTrace("WebSocket received {Length} bytes", payload.Length);
            IOResult writeResult = await upChannel.WriteAsync(new ReadOnlySequence<byte>(payload), token);
            if (writeResult is not IOResult.Ok)
            {
                break;
            }
        }

        await upChannel.WriteEofAsync(token);
    }

    private static async Task WriteToWebSocketAsync(SocketWebSocket webSocket, IChannel upChannel, ILogger? logger, CancellationToken token)
    {
        await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync(token))
        {
            if (webSocket.State is not WebSocketState.Open)
            {
                break;
            }

            logger?.LogTrace("WebSocket sending {Length} bytes", data.Length);
            await webSocket.SendAsync(data.ToArray(), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken: token);
        }
    }

    private static async Task CloseWebSocketAsync(SocketWebSocket webSocket, CancellationToken token)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
        }
    }

    private static Multiaddress ToWebSocketMultiAddress(IPAddress address, PeerId peerId) =>
        Multiaddress.Decode($"/{(address.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6")}/{address}/tcp/0/ws/p2p/{peerId}");

    private static IPEndPoint ToListenEndpoint(Multiaddress address)
    {
        MultiaddressProtocol hostProtocol = address.Has<IP4>() ? address.Get<IP4>()
            : address.Has<IP6>() ? address.Get<IP6>()
            : throw new Libp2pSetupException("WebSocket listeners require an ip4 or ip6 multiaddress.");

        return new IPEndPoint(IPAddress.Parse(hostProtocol.ToString()), int.Parse(address.Get<TCP>().ToString()));
    }

    private static Multiaddress ToRemoteMultiAddress(HttpContext httpContext, bool secure)
    {
        IPAddress remoteAddress = httpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        int remotePort = httpContext.Connection.RemotePort;
        string ipProtocol = remoteAddress.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6";
        string wsProtocol = secure ? "wss" : "ws";
        return Multiaddress.Decode($"/{ipProtocol}/{remoteAddress}/tcp/{remotePort}/{wsProtocol}");
    }

    private static Multiaddress ReplaceEphemeralPort(Multiaddress listenAddr, WebApplication app)
    {
        if (int.Parse(listenAddr.Get<TCP>().ToString()) is not 0)
        {
            return listenAddr;
        }

        IServer server = app.Services.GetRequiredService<IServer>();
        string? boundAddress = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (boundAddress is null)
        {
            throw new Libp2pException("Kestrel did not report the bound WebSocket listener address.");
        }

        return listenAddr.ReplaceOrAdd<TCP>(new Uri(boundAddress).Port);
    }

    private static Uri ToUri(Multiaddress address)
    {
        string scheme = address.Has<WebSocketSecure>() ? "wss" : "ws";
        string host = GetHost(address);
        int port = int.Parse(address.Get<TCP>().ToString());
        return new UriBuilder(scheme, host, port).Uri;
    }

    private static string GetHost(Multiaddress address)
    {
        if (address.Has<IP4>())
        {
            return address.Get<IP4>().ToString();
        }

        if (address.Has<IP6>())
        {
            return address.Get<IP6>().ToString();
        }

        if (address.Has<DNS>())
        {
            return address.Get<DNS>().ToString();
        }

        if (address.Has<DNS4>())
        {
            return address.Get<DNS4>().ToString();
        }

        if (address.Has<DNS6>())
        {
            return address.Get<DNS6>().ToString();
        }

        throw new Libp2pSetupException("WebSocket dial addresses require ip4, ip6, dns, dns4, or dns6.");
    }
}
