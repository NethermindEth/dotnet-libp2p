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
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.IpTcp.Tests")]

namespace Nethermind.Libp2p.Protocols;

public class IpTcpProtocol : ITransportProtocol
{
    private readonly ILogger? _logger;
    private readonly ArrayPool<byte> _receiveBufferPool;

    public IpTcpProtocol(ILoggerFactory? loggerFactory = null) : this(loggerFactory, ArrayPool<byte>.Shared)
    {
    }

    internal IpTcpProtocol(ILoggerFactory? loggerFactory, ArrayPool<byte> receiveBufferPool)
    {
        _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();
        _receiveBufferPool = receiveBufferPool;
    }

    private static Multiaddress ToTcpMultiAddress(IPAddress a, PeerId peerId) => Multiaddress.Decode($"/{(a.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6")}/{a}/tcp/0/p2p/{peerId}");

    internal static async ValueTask<bool> SendAllAsync(Socket socket, ReadOnlySequence<byte> data, CancellationToken token = default)
    {
        int length = checked((int)data.Length);
        if (length is 0)
        {
            return socket.Connected;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            data.CopyTo(buffer);
            ReadOnlyMemory<byte> remaining = buffer.AsMemory(0, length);
            while (!remaining.IsEmpty)
            {
                int sent = await socket.SendAsync(remaining, SocketFlags.None, token);
                if (sent is 0)
                {
                    return false;
                }

                remaining = remaining[sent..];
            }

            return socket.Connected;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }


    public string Id => "ip-tcp";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [.. IpHelper.GetListenerAddresses().Select(a => ToTcpMultiAddress(a, peerId))];

    public static bool IsAddressMatch(Multiaddress addr) => addr.Has<TCP>() && !addr.Has<WebSocket>() && !addr.Has<WebSocketSecure>();

    private static readonly ConditionalWeakTable<object, Admission> Admissions = new();

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        using Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint endpoint = listenAddr.ToEndPoint();
        listener.Bind(endpoint);
        listener.Listen();
        context.Activity?.SetTag("listen-on-addr", endpoint.Address);
        context.Activity?.SetTag("listen-on-port", endpoint.Port);
        if (endpoint.Port == 0) listenAddr.ReplaceOrAdd<TCP>(((IPEndPoint)listener.LocalEndPoint!).Port);
        Admission admission = Admissions.GetValue(context.ConnectionAdmissionScope, _ => new Admission(context.ResourceLimits));
        HashSet<Task> active = [];
        Exception? setupFailure = null;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenRegistration listenerCancellation = lifetime.Token.Register(listener.Close);
        _logger?.LogDebug("Ready to handle connections at {Address}", listenAddr);
        context.ListenerReady(listenAddr);
        context.Activity?.AddEvent(new ActivityEvent("ready"));
        try
        {
            while (true)
            {
                Socket client = await listener.AcceptAsync(lifetime.Token);
                IDisposable? permit;
                string scope;
                try
                {
                    IPAddress source = (client.RemoteEndPoint as IPEndPoint
                        ?? throw new Libp2pException("RemoteEndPoint is not set for accepted connection.")).Address;
                    permit = admission.TryAcquire(source, out scope);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                if (permit is null)
                {
                    client.Dispose();
                    Core.Metrics.Libp2pMetrics.InboundConnectionsRefused.Add(1, new KeyValuePair<string, object?>("scope", scope));
                    continue;
                }
                Task handler = HandleAcceptedAsync(client, permit);
                lock (active) active.Add(handler);
                _ = handler.ContinueWith(t =>
                {
                    _ = t.Exception;
                    lock (active) active.Remove(t);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception failure) when (lifetime.IsCancellationRequested && failure is OperationCanceledException or ObjectDisposedException or SocketException) { }
        finally
        {
            try { lifetime.Cancel(); }
            finally
            {
                Task[] pending;
                lock (active) pending = [.. active];
                await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        if (setupFailure is not null and not ChannelClosedException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(setupFailure).Throw();

        async Task HandleAcceptedAsync(Socket client, IDisposable permit)
        {
            using (permit)
            using (client)
            {
                try
                {
                    await RunConnectionAsync(client, context, null, lifetime.Token, failure =>
                    {
                        Interlocked.CompareExchange(ref setupFailure, failure, null);
                        lifetime.Cancel();
                    });
                }
                catch (Exception failure) { _logger?.LogDebug(failure, "Inbound TCP connection ended"); }
            }
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint endpoint = remoteAddr.ToEndPoint();
        _logger?.LogDebug("Dialling {Address}:{Port}", endpoint.Address, endpoint.Port);
        try { await client.ConnectAsync(endpoint, token); }
        catch (SocketException failure)
        {
            _logger?.LogDebug("Failed to connect {Address}", remoteAddr);
            _logger?.LogTrace(failure, "TCP connection failed");
            throw;
        }
        await RunConnectionAsync(client, context, remoteAddr, token);
    }

    private async Task RunConnectionAsync(Socket client, ITransportContext context, Multiaddress? remoteAddr, CancellationToken token, Action<Exception>? onSetupFailure = null)
    {
        INewConnectionContext? connection = null;
        IChannel? channel = null;
        bool started = false;
        try
        {
            connection = context.CreateConnection();
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Token);
            using CancellationTokenRegistration closeSocket = lifetime.Token.Register(client.Close);
            EndPoint remoteEndpoint = client.RemoteEndPoint ?? throw new Libp2pException("RemoteEndPoint is not set for client connection.");
            EndPoint localEndpoint = client.LocalEndPoint ?? throw new Libp2pException("LocalEndPoint is not set for client connection.");
            connection.State.RemoteAddress = remoteEndpoint.ToMultiaddress(ProtocolType.Tcp);
            connection.State.LocalAddress = localEndpoint.ToMultiaddress(ProtocolType.Tcp);
            // A listener activity spans all accepts: log connections rather than retaining an event per accept.
            _logger?.LogDebug("Connected {Endpoint}", remoteEndpoint);
            if (remoteAddr?.Get<P2P>() is { } requestedPeerId) connection.State.RemoteAddress.Add(requestedPeerId);
            channel = connection.Upgrade();
            started = true;
            using CancellationTokenRegistration abortChannel = lifetime.Token.Register(() =>
            {
                if (!channel.GetAwaiter().IsCompleted) _ = channel.AbortAsync();
            });
#if DEBUG
            TriggerDisconnectionEvent disconnect = localPeerId =>
            {
                if (localPeerId is null || context.Peer.Identity.PeerId == localPeerId) lifetime.Cancel();
            };
            lock (DisconnectionLock) TriggerDisconnection += disconnect;
#endif
            Task<bool> receive = ReceiveAsync();
            Task<bool> send = SendAsync();
            bool orderly = false;
            try
            {
                Task<bool> completed = await Task.WhenAny(receive, send);
                orderly = completed.IsCompletedSuccessfully && completed.Result && !lifetime.IsCancellationRequested;
            }
            finally
            {
                try
                {
                    try
                    {
                        if (orderly) await channel.CloseAsync();
                        else await channel.AbortAsync();
                    }
                    finally { lifetime.Cancel(); }
                }
                finally
                {
                    client.Dispose();
                    try { await ((Task)Task.WhenAll(receive, send)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
                    finally
                    {
#if DEBUG
                        RemoveDisconnectionHandler(disconnect);
#endif
                    }
                }
            }

            async Task<bool> ReceiveAsync()
            {
                int capacity = client.ReceiveBufferSize;
                byte[] scratch = _receiveBufferPool.Rent(capacity);
                try
                {
                    while (true)
                    {
                        int length = await client.ReceiveAsync(scratch.AsMemory(0, capacity), SocketFlags.None, lifetime.Token);
                        // WaitAll readers may retain fragments: publish only compact, owned storage.
                        if (length == 0)
                        {
                            await channel.WriteEofAsync(lifetime.Token);
                            return true;
                        }
                        IOResult result = await channel.WriteAsync(new ReadOnlySequence<byte>(scratch.AsSpan(0, length).ToArray()), lifetime.Token);
                        if (result != IOResult.Ok) return result == IOResult.Ended;
                    }
                }
                finally { _receiveBufferPool.Return(scratch); }
            }

            async Task<bool> SendAsync()
            {
                await foreach (ReadOnlySequence<byte> data in channel.ReadAllAsync(lifetime.Token))
                    if (!await SendAllAsync(client, data, lifetime.Token)) return false;
                return true;
            }
        }
        catch (Exception failure)
        {
            if (!started) onSetupFailure?.Invoke(failure);
            throw;
        }
        finally
        {
            client.Dispose();
            try
            {
                if (channel is not null && !channel.GetAwaiter().IsCompleted) await channel.AbortAsync();
            }
            finally
            {
                try { connection?.Dispose(); }
                finally { if (connection is not null) await connection.Completion; }
            }
        }
    }

    internal sealed class Admission(PeerResourceLimits limits)
    {
        private readonly Dictionary<IPAddress, int> _sources = [];
        private readonly Lock _lock = new();
        private int _count;
        internal int Count { get { lock (_lock) return _count; } }
        internal int Sources { get { lock (_lock) return _sources.Count; } }

        internal IDisposable? TryAcquire(IPAddress source, out string scope)
        {
            if (source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
            lock (_lock)
            {
                scope = "total";
                if (_count >= limits.MaxInboundTcpConnections) return null;
                scope = "source";
                _sources.TryGetValue(source, out int count);
                if (count >= limits.MaxInboundTcpConnectionsPerIp) return null;
                _sources[source] = count + 1;
                _count++;
                return new Permit(this, source);
            }
        }

        private sealed class Permit(Admission owner, IPAddress source) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (owner._lock)
                {
                    owner._count--;
                    if (--owner._sources[source] == 0) owner._sources.Remove(source);
                }
            }
        }
    }

#if DEBUG
    private static readonly Lock DisconnectionLock = new();

    private static void RemoveDisconnectionHandler(TriggerDisconnectionEvent handler)
    {
        lock (DisconnectionLock)
            TriggerDisconnection = (TriggerDisconnectionEvent?)Delegate.Remove(TriggerDisconnection, handler) ?? (_ => { });
    }

    public static TriggerDisconnectionEvent TriggerDisconnection = (_) => { };

    public delegate void TriggerDisconnectionEvent(PeerId? localPeerId = null);
#endif
}
